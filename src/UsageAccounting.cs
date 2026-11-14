using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TokLens
{
    // A correction replaces a complete day/tool partition. Field-wise MAX can
    // combine mutually exclusive versions of the same requests into a fake bill.
    internal static class UsageAccounting
    {
        public const int Revision = 29;
        public const int ReconciledRevision = 28;

        public static string Partition(string date, string tool)
        {
            return (date ?? "") + "||" + (tool ?? "");
        }

        public static bool HasRequestLedger(string tool)
        {
            return tool == "Claude Code" || tool == "Claude Desktop" || tool == "Claude Code (WSL)" ||
                   tool == "OpenAI Codex" || tool == "OpenAI Codex (WSL)" || tool == "Google Antigravity" ||
                   tool == "ZCode" || tool == "ZCode (WSL)";
        }

        public static long UtcMilliseconds()
        {
            return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L;
        }

        public static RecordBucket PickWholeBucket(RecordBucket current, RecordBucket historical)
        {
            if (current == null) return historical;
            if (historical == null) return current;
            if (current.Turns != historical.Turns)
                return current.Turns > historical.Turns ? current : historical;
            return current.TotalTokens >= historical.TotalTokens ? current : historical;
        }

        public static string AntigravityModel(string backend, string tier)
        {
            string actual = PricingEngine.NormalizeModelId(backend, "unknown");
            string variant = PricingEngine.NormalizeModelId(tier, "");
            // Field 21 can be a generic display label ("Gemini Flash"). Only
            // accept a tier belonging to this backend, never replace a worker
            // with its parent's selected model.
            if (variant.StartsWith(actual + "-", StringComparison.Ordinal) || variant == actual)
                return variant;
            if (actual == "gemini-pro-orchestrator" && variant.StartsWith("orchestrator-", StringComparison.Ordinal))
                return variant;
            return actual;
        }
    }

    // Minimal protobuf wire reader. Associations come from actual field paths,
    // not byte searches, generation index + 1, or a conversation's last model.
    internal static class UsageProto
    {
        private static bool Varint(byte[] data, ref int pos, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64 && pos < data.Length; shift += 7)
            {
                byte b = data[pos++];
                if (shift == 63 && b > 1) return false;
                value |= (ulong)(b & 127) << shift;
                if (b < 128) return true;
            }
            return false;
        }

        private static bool Field(byte[] data, int field, int wire, out byte[] bytes, out ulong number)
        {
            bytes = null;
            number = 0;
            if (data == null) return false;
            int pos = 0;
            while (pos < data.Length)
            {
                ulong tag, value;
                if (!Varint(data, ref pos, out tag) || tag == 0) return false;
                int type = (int)(tag & 7);
                int start = pos, length = 0;
                if (type == 0)
                {
                    if (!Varint(data, ref pos, out value)) return false;
                    if ((int)(tag >> 3) == field && wire == 0) { number = value; return true; }
                    continue;
                }
                if (type == 2)
                {
                    if (!Varint(data, ref pos, out value) || value > (ulong)(data.Length - pos)) return false;
                    start = pos;
                    length = (int)value;
                }
                else if (type == 1) length = 8;
                else if (type == 5) length = 4;
                else return false;
                if (length > data.Length - pos) return false;
                pos += length;
                if ((int)(tag >> 3) == field && type == wire)
                {
                    bytes = new byte[length];
                    Buffer.BlockCopy(data, start, bytes, 0, length);
                    return true;
                }
            }
            return false;
        }

        public static byte[] Message(byte[] data, int field)
        {
            byte[] bytes; ulong number;
            return Field(data, field, 2, out bytes, out number) ? bytes : null;
        }

        public static string Text(byte[] data, int field)
        {
            byte[] bytes = Message(data, field);
            return bytes == null ? "" : Encoding.UTF8.GetString(bytes);
        }

        public static bool Number(byte[] data, int field, out long number)
        {
            byte[] bytes; ulong value;
            bool found = Field(data, field, 0, out bytes, out value) && value <= long.MaxValue;
            number = found ? (long)value : 0L;
            return found;
        }
    }
}
