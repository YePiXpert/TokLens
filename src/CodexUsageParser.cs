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
        private readonly HashSet<string> _intentRecorded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string,long[]> _uncheckpointed = new Dictionary<string,long[]>(StringComparer.Ordinal);
        private readonly HashSet<string> _compactions = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _snapshots = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _snapshotOrder = new Queue<string>();
        private long[] _previous;
        private string _pending = "";
        private string _pendingKey = "";
        private string _pendingThread = "";
        private long _started;
        public string ThreadId;
        public string Model = "unknown";
        public string ForkedFrom = "";
        public long ForkedAtMs;
        public bool SpawnedSubagent;
        public readonly List<UsageEvent> Events = new List<UsageEvent>();
        private readonly UsageRecordMetadata _requestContext = new UsageRecordMetadata();

        public CodexUsageParser(string tool, string tid, DateTime fallback, string meta, List<UsageEvent> previousEvents)
        {
            _tool = tool; ThreadId = tid ?? ""; _fallback = fallback;
            _json.MaxJsonLength = int.MaxValue;
            if (previousEvents != null)
                foreach (UsageEvent ev in previousEvents) if (ev != null)
                {
                    Put(ev.Clone());
                    if (!string.IsNullOrEmpty(meta) && !string.IsNullOrEmpty(ev.DedupKey)) _intentRecorded.Add(ev.DedupKey);
                }
            if (!string.IsNullOrEmpty(meta) && meta.StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, object> state = Dict(_json.DeserializeObject(meta));
                Model = Text(state, "mdl") ?? Model;
                ThreadId = Text(state, "tid") ?? ThreadId;
                ForkedFrom = Text(state, "parent") ?? "";
                ForkedAtMs = Number(state, "fork_ms");
                SpawnedSubagent = Text(state, "spawned") == "True" || Text(state, "spawned") == "true";
                _started = Number(state, "started");
                _pending = Text(state, "pending") ?? "";
                _pendingKey = Text(state, "pending_key") ?? "";
                _pendingThread = Text(state, "pending_thread") ?? "";
                UsageServiceTier.DeserializeInto(Text(state,"usage_metadata"),_requestContext);
                object value;
                if (state != null && state.TryGetValue("previous", out value) && value is IList)
                {
                    IList a = (IList)value;
                    if (a.Count == 4) _previous = new long[] { Convert.ToInt64(a[0]), Convert.ToInt64(a[1]), Convert.ToInt64(a[2]), Convert.ToInt64(a[3]) };
                }
                if (state != null && state.TryGetValue("seen", out value) && value is IList)
                    foreach (object item in (IList)value) Remember(Convert.ToString(item, CultureInfo.InvariantCulture));
                if (state != null && state.TryGetValue("compactions",out value)&&value is IList)
                    foreach(object item in (IList)value)_compactions.Add(Convert.ToString(item,CultureInfo.InvariantCulture));
                Dictionary<string,object> pendingBatch=Dict(Value(state,"uncheckpointed"));
                if(pendingBatch!=null)foreach(KeyValuePair<string,object> item in pendingBatch)
                {
                    IList a=item.Value as IList;
                    if(a!=null&&a.Count==4)_uncheckpointed[item.Key]=new long[]{Convert.ToInt64(a[0]),Convert.ToInt64(a[1]),Convert.ToInt64(a[2]),Convert.ToInt64(a[3])};
                }
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

        private static bool ValidEmbeddedUsage(Dictionary<string,object> usage)
        {
            if(usage==null||!usage.ContainsKey("input_tokens")||!usage.ContainsKey("output_tokens"))return false;
            foreach(string key in new[]{"input_tokens","output_tokens","cached_input_tokens","cache_read_input_tokens","cached_tokens","cache_write_input_tokens","cache_creation_input_tokens"})
            {
                long value;
                if(usage.ContainsKey(key)&&(!long.TryParse(Text(usage,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out value)||value<0||value>1000000000000000L))return false;
            }
            long input=Number(usage,"input_tokens"),output=Number(usage,"output_tokens");
            long read=Math.Max(Number(usage,"cached_input_tokens"),Math.Max(Number(usage,"cache_read_input_tokens"),Number(usage,"cached_tokens")));
            long write=Math.Max(Number(usage,"cache_write_input_tokens"),Number(usage,"cache_creation_input_tokens"));
            return input+output>0&&read<=input&&write<=input-read;
        }

        private void ReadNativeRecord(Dictionary<string,object> p,string ts,long ms)
        {
            long[] usage=Counters(Dict(Value(p,"usage")));
            if(usage==null)return;
            string response=Text(p,"response_id");
            bool knownResponse=!string.IsNullOrEmpty(response)&&_intentRecorded.Contains("codex-response:"+response);
            string previousPendingKey=_pendingKey,previousPendingThread=_pendingThread;
            _pendingKey=Emit(usage,ts,ms,string.IsNullOrEmpty(response)?null:"codex-response:"+response);
            if(!knownResponse||_uncheckpointed.ContainsKey(_pendingKey))_uncheckpointed[_pendingKey]=usage;
            int metadataIndex;
            if(_indexes.TryGetValue(_pendingKey,out metadataIndex))
            {
                UsageServiceTier.ReadEvidence(Events[metadataIndex],p,"request");
                string responseModel=Text(p,"model")??Text(p,"model_name");
                if(!string.IsNullOrWhiteSpace(responseModel))Events[metadataIndex].Model=PricingEngine.NormalizeModelId(responseModel,"unknown");
            }
            int timingIndex;
            long elapsed=Number(p,"duration_ms");if(elapsed==0)elapsed=Number(p,"durationMs");
            if(elapsed>0&&elapsed<=int.MaxValue&&_indexes.TryGetValue(_pendingKey,out timingIndex))
            {Events[timingIndex].DurationMs=(int)elapsed;Events[timingIndex].TimingSource="request";}
            _pending=Signature(usage);
            long[] threadTotals=Counters(Dict(Value(p,"thread_token_usage")));
            _pendingThread=threadTotals!=null?Signature(threadTotals):_pendingKey==previousPendingKey?previousPendingThread:"";
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
                UsageEvent previous=Events[index];
                // The same response can be replayed under a later turn's context.
                // A model explicitly carried by that response is applied after Put.
                if (previous.Model != "unknown" && (_intentRecorded.Contains(key) || ev.Model == "unknown")) ev.Model = previous.Model;
                // A replay of the same response must not inherit settings from a later turn.
                if(UsageServiceTier.Normalize(previous.RequestedServiceTier)!="unknown" || _intentRecorded.Contains(key))
                {
                    ev.RequestedServiceTier=previous.RequestedServiceTier;
                    if(UsageServiceTier.Normalize(ev.ActualServiceTier)=="unknown")ev.TierEvidence=previous.TierEvidence;
                }
                if(UsageServiceTier.Normalize(ev.ActualServiceTier)=="unknown"&&UsageServiceTier.Normalize(previous.ActualServiceTier)!="unknown")
                {ev.ActualServiceTier=previous.ActualServiceTier;ev.TierEvidence=previous.TierEvidence;}
                if(ev.Workspace.Length==0)ev.Workspace=previous.Workspace;
                if(ev.ActualContextLimit<=0){ev.ActualContextLimit=previous.ActualContextLimit;ev.ContextLimitEvidence=previous.ContextLimitEvidence;}
                if(ev.LoggedCostUsd<=0)ev.LoggedCostUsd=previous.LoggedCostUsd;
                if(!UsageTiming.IsMeasured(ev.TimingSource)&&UsageTiming.IsMeasured(previous.TimingSource))
                {ev.DurationMs=previous.DurationMs;ev.TimingSource=previous.TimingSource;}
                Events[index] = ev;
            }
            else { if (key.Length > 0) _indexes[key] = Events.Count; Events.Add(ev); }
        }

        public void Reset()
        {
            _previous = null; _pending = ""; _pendingKey = ""; _pendingThread = ""; _started = 0;
            Model = "unknown"; _snapshots.Clear(); _snapshotOrder.Clear();
            _intentRecorded.Clear();
            _uncheckpointed.Clear();
            _compactions.Clear();
            new UsageRecordMetadata().CopyMetadataTo(_requestContext);
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
                Dictionary<string,object> spawn = Dict(Value(Dict(Value(Dict(Value(p,"source")),"subagent")),"thread_spawn"));
                string spawnParent = Text(spawn,"parent_thread_id");
                SpawnedSubagent = !string.IsNullOrEmpty(spawnParent);
                ForkedFrom = Text(p, "forked_from_id") ?? spawnParent ?? "";
                DateTime parsedFork;
                ForkedAtMs = !string.IsNullOrEmpty(ts)&&DateTime.TryParse(ts,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsedFork)?ms:0;
            }
            if (type == "compacted")
            {
                // A compaction can be marked before or after its native record.
                // Equal counts alone cannot pair it with a later ordinary request;
                // an explicit matching thread checkpoint still proves coverage.
                string response = Text(p,"compaction_response_id");
                if (!string.IsNullOrEmpty(response))_compactions.Add("codex-response:"+response);
                Dictionary<string,object> embedded=Dict(Value(p,"latest_token_usage_record"));
                if(!string.IsNullOrEmpty(response)&&embedded!=null&&Text(embedded,"response_id")==response&&ValidEmbeddedUsage(Dict(Value(embedded,"usage"))))
                    ReadNativeRecord(embedded,ts,ms);
                return;
            }
            string pType = Text(p, "type");
            bool isThreadSettings = type == "event_msg" && pType == "thread_settings_applied";
            if (type == "session_meta" || type == "turn_context" || isThreadSettings)
            {
                // Desktop rollouts put the effective settings inside thread_settings.
                // Read the historical event in order, never the user's current config.
                Dictionary<string, object> settings = isThreadSettings ? Dict(Value(p, "thread_settings")) : null;
                Dictionary<string, object> context = settings ?? p;
                string model = Text(context, "model");
                if (!string.IsNullOrWhiteSpace(model)) Model = PricingEngine.NormalizeModelId(model, "unknown");
                string evidence = isThreadSettings ? "thread_settings" : type == "session_meta" ? "session" : "request";
                // An explicit reset/null/unsupported value cannot retain an earlier Fast setting.
                // An absent setting, however, does not overwrite the previous known intent.
                if (context.ContainsKey("service_tier") || context.ContainsKey("requested_service_tier") || context.ContainsKey("fast_mode"))
                { _requestContext.RequestedServiceTier="unknown"; _requestContext.TierEvidence=evidence+":unknown"; }
                UsageServiceTier.ReadEvidence(_requestContext,context,evidence);
                // Configuration describes intent. Actual response tier is never inherited by a later request.
                _requestContext.ActualServiceTier="unknown";
            }
            if (type == "turn_context" || pType == "user_message" || pType == "task_started" ||
                (type == "response_item" && (pType ?? "").EndsWith("_call_output", StringComparison.Ordinal))) _started = ms;

            if (type == "token_usage_record")
            {
                ReadNativeRecord(p,ts,ms);
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
            // A legitimate request can reproduce a prior (total,last) pair after reset.
            // Only expire snapshot signatures; response IDs retain their stronger identity.
            if (reset) { _snapshots.Clear(); _snapshotOrder.Clear(); }
            string snapshot = Signature(total) + "|" + Signature(last);
            bool unchangedTotal = total != null && _previous != null && Signature(total) == Signature(_previous);
            bool duplicate = total != null && !Remember(snapshot);
            // A token_count snapshot is not new usage until its cumulative counters
            // advance. Its last-request fields can be cleared or rewritten in place.
            // Keep processing pending response metadata; an identified usage record
            // remains independently countable even while this snapshot is stale.
            duplicate = duplicate || unchangedTotal;
            long[] usageNow = last;
            if (usageNow == null && total != null)
            {
                usageNow = new long[4];
                for (int i = 0; i < 4; i++) usageNow[i] = _previous == null || reset ? total[i] : Math.Max(0, total[i] - _previous[i]);
            }
            int checkpointIndex;
            bool checkpointCovered=total!=null&&!unchangedTotal&&_pendingThread.Length>0&&_pendingThread==Signature(total)&&_indexes.TryGetValue(_pendingKey,out checkpointIndex);
            bool emitRemainder=false;
            if(checkpointCovered)
            {
                UsageEvent pendingEvent=Events[_indexes[_pendingKey]];
                long[] counted=new long[4];
                foreach(long[] emitted in _uncheckpointed.Values)for(int i=0;i<4;i++)counted[i]+=emitted[i];
                if(_uncheckpointed.Count==0)counted=new long[]{pendingEvent.FreshInput+pendingEvent.CacheRead+pendingEvent.CacheWrite,pendingEvent.Output,pendingEvent.CacheRead,pendingEvent.CacheWrite};
                usageNow=new long[4];
                for(int i=0;i<4;i++)
                {
                    long delta=_previous==null||reset?total[i]:Math.Max(0,total[i]-_previous[i]);
                    usageNow[i]=Math.Max(0,delta-counted[i]);
                }
                emitRemainder=usageNow[0]+usageNow[1]>0;
            }
            if (total != null) _previous = total;
            bool recorded = _pending.Length > 0 && !_compactions.Contains(_pendingKey) && (_pending == Signature(usageNow) || (last != null && last[0] + last[1] == 0));
            recorded=recorded||checkpointCovered;
            int pendingIndex;
            if (recorded && !string.IsNullOrEmpty(explicitModel) && _indexes.TryGetValue(_pendingKey, out pendingIndex)) Events[pendingIndex].Model = Model;
            if(recorded&&_indexes.TryGetValue(_pendingKey,out pendingIndex))UsageServiceTier.ReadEvidence(Events[pendingIndex],info,"request");
            _pending = "";
            _pendingKey = "";
            _pendingThread = "";
            _uncheckpointed.Clear();
            if (!duplicate && (!recorded||emitRemainder) && usageNow != null)
            {
                if (total != null || Remember((ts ?? "") + "|" + Signature(usageNow)))
                {
                    string checkpointKey=emitRemainder?"codex-checkpoint:"+ThreadId+":"+ms.ToString(CultureInfo.InvariantCulture)+":"+Signature(total):null;
                    string key=Emit(usageNow, ts, ms, checkpointKey);int index;
                    if(_indexes.TryGetValue(key,out index))UsageServiceTier.ReadEvidence(Events[index],info,"request");
                }
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
            _requestContext.CopyMetadataTo(ev);
            if(ev.Provider=="unknown")ev.Provider=UsageServiceTier.InferProvider(Model);
            ev.SessionId = ThreadId;
            Put(ev);
            _intentRecorded.Add(requestKey);
            if (ms > 0) _started = ms;
            return requestKey;
        }

        public string SerializeState()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["mdl"] = Model; state["tid"] = ThreadId; state["parent"] = ForkedFrom; state["fork_ms"] = ForkedAtMs;state["spawned"]=SpawnedSubagent;
            state["usage_metadata"]=UsageServiceTier.Serialize(_requestContext);
            state["uncheckpointed"]=_uncheckpointed;
            state["compactions"]=new List<string>(_compactions).ToArray();
            state["previous"] = _previous; state["pending"] = _pending; state["pending_key"] = _pendingKey; state["pending_thread"]=_pendingThread;state["started"] = _started; state["seen"] = _snapshotOrder.ToArray();
            return _json.Serialize(state);
        }
    }
}
