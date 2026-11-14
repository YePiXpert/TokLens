using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace TokLens
{
    // response_id is the strongest identity. token_usage_record and the next
    // token_count often describe the same request and must only be billed once.
    internal sealed class CodexUsageParser
    {
        private readonly string _tool;
        private readonly DateTime _fallback;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Dictionary<string, int> _indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _snapshots = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _snapshotOrder = new Queue<string>();
        private long[] _previous;
        private string _pending = "";
        private string _pendingKey = "";
        private long _started;
        public string ThreadId;
        public string Model = "unknown";
        public string ForkedFrom = "";
        public long ForkedAtMs;
        public readonly List<UsageEvent> Events = new List<UsageEvent>();

        public CodexUsageParser(string tool, string tid, DateTime fallback, string meta, List<UsageEvent> previousEvents)
        {
            _tool = tool; ThreadId = tid ?? ""; _fallback = fallback;
            _json.MaxJsonLength = int.MaxValue;
            if (previousEvents != null)
                foreach (UsageEvent ev in previousEvents) if (ev != null) Put(ev.Clone());
            if (!string.IsNullOrEmpty(meta) && meta.StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, object> state = Dict(_json.DeserializeObject(meta));
                Model = Text(state, "mdl") ?? Model;
                ThreadId = Text(state, "tid") ?? ThreadId;
                ForkedFrom = Text(state, "parent") ?? "";
                ForkedAtMs = Number(state, "fork_ms");
                _started = Number(state, "started");
                _pending = Text(state, "pending") ?? "";
                _pendingKey = Text(state, "pending_key") ?? "";
                object value;
                if (state != null && state.TryGetValue("previous", out value) && value is IList)
                {
                    IList a = (IList)value;
                    if (a.Count == 4) _previous = new long[] { Convert.ToInt64(a[0]), Convert.ToInt64(a[1]), Convert.ToInt64(a[2]), Convert.ToInt64(a[3]) };
                }
                if (state != null && state.TryGetValue("seen", out value) && value is IList)
                    foreach (object item in (IList)value) Remember(Convert.ToString(item, CultureInfo.InvariantCulture));
            }
        }

        private static Dictionary<string, object> Dict(object value) { return value as Dictionary<string, object>; }
        private static object Value(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        private static string Text(Dictionary<string, object> d, string k) { object v = Value(d, k); return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
        private static long Number(Dictionary<string, object> d, string k)
        {
            long n; return long.TryParse(Text(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? Math.Max(0L, Math.Min(1000000000000000L, n)) : 0L;
        }

        private static long[] Counters(Dictionary<string, object> usage)
        {
            if (usage == null) return null;
            long input = Number(usage, "input_tokens");
            long read = Math.Min(input, Math.Max(Number(usage, "cached_input_tokens"), Math.Max(Number(usage, "cache_read_input_tokens"), Number(usage, "cached_tokens"))));
            long write = Math.Min(input - read, Math.Max(Number(usage, "cache_write_input_tokens"), Number(usage, "cache_creation_input_tokens")));
            return new long[] { input, Number(usage, "output_tokens"), read, write };
        }

        private static string Signature(long[] usage) { return usage == null ? "-" : string.Join(",", Array.ConvertAll(usage, delegate(long n) { return n.ToString(CultureInfo.InvariantCulture); })); }

        private bool Remember(string key)
        {
            if (!_snapshots.Add(key)) return false;
            _snapshotOrder.Enqueue(key);
            while (_snapshotOrder.Count > 128) _snapshots.Remove(_snapshotOrder.Dequeue());
            return true;
        }

        private void Put(UsageEvent ev)
        {
            string key = ev.DedupKey ?? "";
            int index;
            if (key.Length > 0 && _indexes.TryGetValue(key, out index))
            {
                if (ev.TimestampMs < Events[index].TimestampMs) return;
                if (ev.Model == "unknown" && Events[index].Model != "unknown") ev.Model = Events[index].Model;
                Events[index] = ev;
            }
            else { if (key.Length > 0) _indexes[key] = Events.Count; Events.Add(ev); }
        }

        public void Reset()
        {
            _previous = null; _pending = ""; _pendingKey = ""; _started = 0;
            Model = "unknown"; _snapshots.Clear(); _snapshotOrder.Clear();
        }

        public void OnLine(string line, int unused)
        {
            Dictionary<string, object> obj = Dict(_json.DeserializeObject(line));
            string type = Text(obj, "type");
            Dictionary<string, object> p = Dict(Value(obj, "payload"));
            if (p == null) return;
            string ts = Text(obj, "timestamp");
            long ms = TokenScanner.UsageTimestampMs(ts, _fallback);
            if (type == "session_meta")
            {
                ThreadId = Text(p, "id") ?? ThreadId;
                ForkedFrom = Text(p, "forked_from_id") ?? "";
                ForkedAtMs = ms;
            }
            string pType = Text(p, "type");
            if (type == "session_meta" || type == "turn_context" || pType == "thread_settings_applied")
            {
                string model = Text(p, "model");
                if (!string.IsNullOrWhiteSpace(model)) Model = PricingEngine.NormalizeModelId(model, "unknown");
            }
            if (type == "turn_context" || pType == "user_message" || pType == "task_started" ||
                (type == "response_item" && (pType ?? "").EndsWith("_call_output", StringComparison.Ordinal))) _started = ms;

            if (type == "token_usage_record")
            {
                long[] usage = Counters(Dict(Value(p, "usage")));
                if (usage == null) return;
                string response = Text(p, "response_id");
                _pendingKey = Emit(usage, ts, ms, string.IsNullOrEmpty(response) ? null : "codex-response:" + response);
                int timingIndex;
                long elapsed = Number(p, "duration_ms");
                if (elapsed == 0) elapsed = Number(p, "durationMs");
                if (elapsed > 0 && elapsed <= int.MaxValue && _indexes.TryGetValue(_pendingKey, out timingIndex))
                { Events[timingIndex].DurationMs = (int)elapsed; Events[timingIndex].TimingSource = "request"; }
                _pending = Signature(usage);
                return;
            }
            if (type != "event_msg" || pType != "token_count") return;
            Dictionary<string, object> info = Dict(Value(p, "info"));
            if (info == null) return;
            string explicitModel = Text(info, "model") ?? Text(info, "model_name") ?? Text(p, "model");
            if (!string.IsNullOrEmpty(explicitModel)) Model = PricingEngine.NormalizeModelId(explicitModel, "unknown");
            long[] last = Counters(Dict(Value(info, "last_token_usage")));
            long[] total = Counters(Dict(Value(info, "total_token_usage")));
            // A reset starts a new cumulative segment. Keeping a component-wise
            // high-water baseline would hide every request until it catches up.
            bool reset = total != null && _previous != null &&
                total[0] + total[1] < _previous[0] + _previous[1];
            if (reset && last == null) { _snapshots.Clear(); _snapshotOrder.Clear(); }
            string snapshot = Signature(total) + "|" + Signature(last);
            bool duplicate = total != null && !Remember(snapshot);
            long[] usageNow = last;
            if (usageNow == null && total != null)
            {
                usageNow = new long[4];
                for (int i = 0; i < 4; i++) usageNow[i] = _previous == null || reset ? total[i] : Math.Max(0, total[i] - _previous[i]);
            }
            if (total != null) _previous = total;
            bool recorded = _pending.Length > 0 && (_pending == Signature(usageNow) || (last != null && last[0] + last[1] == 0));
            int pendingIndex;
            if (recorded && !string.IsNullOrEmpty(explicitModel) && _indexes.TryGetValue(_pendingKey, out pendingIndex)) Events[pendingIndex].Model = Model;
            _pending = "";
            _pendingKey = "";
            if (!duplicate && !recorded && usageNow != null)
            {
                if (total != null || Remember((ts ?? "") + "|" + Signature(usageNow))) Emit(usageNow, ts, ms, null);
            }
        }

        private string Emit(long[] usage, string ts, long ms, string requestKey)
        {
            if (usage[0] + usage[1] <= 0) return "";
            long read = Math.Min(usage[0], usage[2]), write = Math.Min(usage[0] - read, usage[3]);
            if (string.IsNullOrEmpty(requestKey)) requestKey = "codex:" + ThreadId + ":" + ms.ToString(CultureInfo.InvariantCulture) + ":" + Signature(usage);
            long duration = ms - _started;
            UsageEvent ev = new UsageEvent(_tool, Model, TokenScanner.UsageLocalDate(ts, _fallback), usage[0] - read - write, usage[1], read, write, 0, 1, requestKey, ms,
                _started > 0 && duration >= 100 && duration <= 600000 ? (int)duration : 0);
            ev.SessionId = ThreadId;
            Put(ev);
            if (ms > 0) _started = ms;
            return requestKey;
        }

        public string SerializeState()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["mdl"] = Model; state["tid"] = ThreadId; state["parent"] = ForkedFrom; state["fork_ms"] = ForkedAtMs;
            state["previous"] = _previous; state["pending"] = _pending; state["pending_key"] = _pendingKey; state["started"] = _started; state["seen"] = _snapshotOrder.ToArray();
            return _json.Serialize(state);
        }
    }
}
