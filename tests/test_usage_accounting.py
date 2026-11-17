import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest import mock


spec = importlib.util.spec_from_file_location("toklens_agent", Path(__file__).resolve().parents[1] / "vps" / "toklens_agent.py")
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


def usage(inp=100, out=20, read=60, write=10):
    return dict(input_tokens=inp, output_tokens=out, cached_input_tokens=read, cache_write_input_tokens=write, reasoning_output_tokens=out // 2)


def frame(kind, payload, second=1):
    return json.dumps(dict(type=kind, payload=payload, timestamp="2026-11-12T12:00:%02dZ" % second))


def count(last, total, second=1):
    return frame("event_msg", dict(type="token_count", info=dict(last_token_usage=last, total_token_usage=total)), second)


def varint(n):
    result = bytearray()
    while n >= 128:
        result.append((n & 127) | 128)
        n >>= 7
    result.append(n)
    return bytes(result)


def field(number, data):
    return varint((number << 3) | 2) + varint(len(data)) + data


def row(model, fresh, cached, turns=1):
    return agent.normalize_record_row(["2026-11-12", "OpenAI Codex", model, turns, fresh, cached, 0, 0, 1.0, 1.0, "test"])


class AccountingTests(unittest.TestCase):
    def test_tier_baseline_and_mixed_long_context_rates(self):
        with tempfile.TemporaryDirectory(prefix="toklens-tier-") as temp:
            scanner = agent.MultiHarnessScanner(Path(temp), allow_network_pricing=False)
            tier = {"actual_tier": "fast", "provider": "openai"}
            scanner.add_ev("OpenAI Codex", "gpt-6.1-sol", "2026-11-17", 1000, 200, 500, 100, 0, tier=tier)
            first = scanner.build_records()[0]
            self.assertAlmostEqual(first[9], .0104, places=6)
            scanner.add_ev("OpenAI Codex", "gpt-6.1-sol", "2026-11-17", 300000, 200, 500, 100, 0, tier=tier)
            rows = scanner.build_records()
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0][10], "多种费率 · 见明细")

    def test_shared_service_tier_fixtures(self):
        fixtures = json.loads((Path(__file__).parent / "fixtures" / "service_tier_cases.json").read_text())
        for case in fixtures["cases"]:
            with self.subTest(case=case["id"]), tempfile.TemporaryDirectory(prefix="toklens-tier-") as temp:
                scanner = agent.MultiHarnessScanner(Path(temp), allow_network_pricing=False)
                scanner.add_ev("OpenAI Codex", case["model"], case["date"], case["fi"], case["output"], case["cache_read"], case["cache_write"], case["reported"], tier={"requested_tier":case["requested"], "actual_tier":case["actual"], "provider":case["provider"], "tier_evidence":"fixture"})
                actual = scanner.build_records()[0]
                self.assertEqual(len(actual), 19)
                self.assertAlmostEqual(actual[8], case["expected_cost"], places=6)
                self.assertEqual(actual[14], case["expected_basis"])
                for index, name in ((17, "expected_standard"), (18, "expected_premium")):
                    if case[name] is None:
                        self.assertIsNone(actual[index])
                    else:
                        self.assertAlmostEqual(actual[index], case[name], places=6)

    def test_request_preference_never_promoted_to_actual_and_response_overrides(self):
        parser = agent.CodexUsageState("thread", 1794484800)
        parser.on_line(frame("turn_context", {"model":"gpt-6.1-sol", "service_tier":"priority"}, 0))
        parser.on_line(frame("token_usage_record", {"response_id":"a", "usage":usage()}, 1))
        first = list(parser.rows.values())[0][13]
        self.assertEqual((first["requested_tier"], first["actual_tier"]), ("fast", "unknown"))
        parser.on_line(frame("token_usage_record", {"response_id":"b", "usage":usage(), "response":{"service_tier":"default"}}, 2))
        second = list(parser.rows.values())[1][13]
        self.assertEqual((second["requested_tier"], second["actual_tier"]), ("fast", "standard"))

    def test_old_ledger_migration_preserves_unknown_and_tier_buckets(self):
        with tempfile.TemporaryDirectory(prefix="toklens-tier-") as temp:
            home = Path(temp)
            folder = home / ".toklens"
            folder.mkdir()
            db = sqlite3.connect(folder / "ledger.db")
            db.execute("CREATE TABLE daily_archive(rec_date TEXT,tool TEXT,model TEXT,turns INTEGER,fi INTEGER,cr INTEGER,cw INTEGER,out_tok INTEGER,cost_usd REAL,base_usd REAL,rate_label TEXT,PRIMARY KEY(rec_date,tool,model))")
            old = row("gpt-6.1-sol", 1000, 500)
            db.execute("INSERT INTO daily_archive VALUES (?,?,?,?,?,?,?,?,?,?,?)", old[:11])
            db.commit()
            db.close()
            result = agent.merge_with_ledger_db(home, [])
            self.assertEqual(result[0][11:13], ["unknown", "unknown"])
            self.assertTrue(list(folder.glob("ledger.db.before-tier-v30.*.db")))
            self.assertEqual(result, agent.merge_with_ledger_db(home, []))
            fast, standard = list(old), list(old)
            fast[11:17] = ["fast", "fast", "response", "estimated_tier_price", "test", "openai"]
            standard[11:17] = ["standard", "standard", "response", "estimated_tier_price", "test", "openai"]
            combined = agent.merge_with_ledger_db(home, [fast, standard], reconciled_partitions={old[0]+"||"+old[1]:9999999999999})
            self.assertEqual(len(combined), 2)
            self.assertEqual({r[12] for r in combined}, {"fast", "standard"})
            self.assertEqual(combined, agent.merge_with_ledger_db(home, combined))

    def test_fast_suffix_not_silently_priced_as_standard(self):
        with tempfile.TemporaryDirectory(prefix="toklens-tier-") as temp:
            self.assertEqual(agent.PricingLookup(Path(temp), allow_network=False).lookup("gpt-6.1-sol-fast"), (0,0,0,0))

    def test_generic_tier_survives_cursor_and_malformed_files_retry(self):
        with tempfile.TemporaryDirectory(prefix="toklens-tier-") as temp:
            home = Path(temp)
            folder = home / "source"
            folder.mkdir()
            path = folder / "session.jsonl"
            content = [dict(type="turn_context",model="gpt-6.1-sol",service_tier="priority"),
                       dict(timestamp="2026-11-17T01:00:00Z",request_id="one",usage=dict(input_tokens=1000,output_tokens=200),actual_service_tier="priority")]
            path.write_text("\n".join(map(json.dumps, content)) + "\n")
            scanner = agent.MultiHarnessScanner(home, allow_network_pricing=False)
            scanner.scan_jsonl_tree("Fixture", [folder], "wrong-default")
            scanner.cursor_store.flush()
            rows = scanner.build_records()
            self.assertEqual(rows[0][11:13], ["fast", "fast"])
            scanner = agent.MultiHarnessScanner(home, allow_network_pricing=False)
            scanner.scan_jsonl_tree("Fixture", [folder], "wrong-default")
            self.assertEqual(scanner.build_records(), rows)
            path.write_text('{"usage": broken}\n' + json.dumps(dict(timestamp="2026-11-17T01:00:00Z",usage=dict(input_tokens=4,output_tokens=2))) + "\n")
            scanner = agent.MultiHarnessScanner(home, allow_network_pricing=False)
            scanner.scan_jsonl_tree("Fixture", [folder], "wrong-default")
            self.assertIn("Fixture", scanner.failed_exact_tools)
            self.assertEqual(scanner.source_errors["Fixture"], 1)
            self.assertFalse(scanner.cursor_store.dirty)
            self.assertEqual(scanner.build_records(), rows)
            fresh = home / "empty"
            fresh.mkdir()
            (fresh / "unknown.jsonl").write_text(json.dumps(dict(timestamp="2026-11-17T01:00:00Z",usage=dict(input_tokens=4,output_tokens=2))) + "\n")
            scanner = agent.MultiHarnessScanner(home, allow_network_pricing=False)
            scanner.scan_jsonl_tree("Fixture", [fresh], "wrong-default")
            self.assertEqual(scanner.build_records()[0][2], "unknown")
            self.assertEqual(scanner.build_records()[0][14], "unpriced")

    def test_duplicate_response_preserves_actual_tier_after_setting_change(self):
        parser = agent.CodexUsageState("thread", 1794484800)
        parser.on_line(frame("turn_context", {"model":"gpt-6.1-sol","fast_mode":True}, 0))
        parser.on_line(frame("token_usage_record", {"response_id":"a","usage":usage(),"actual_service_tier":"priority"}, 1))
        parser.on_line(frame("turn_context", {"fast_mode":False}, 2))
        parser.on_line(frame("token_usage_record", {"response_id":"a","usage":usage()}, 3))
        self.assertEqual(len(parser.rows), 1)
        self.assertEqual(list(parser.rows.values())[0][13]["actual_tier"], "fast")
        self.assertEqual(list(parser.rows.values())[0][13]["requested_tier"], "fast")

    def test_new_revision_proof_and_legacy_group_never_double_count(self):
        old = row("gpt-6.1-sol", 1000, 500, 5)
        new = list(old)
        new[11:17] = ["fast","fast","response","estimated_tier_price","official","openai"]
        partition = old[0] + "||" + old[1]
        self.assertEqual(agent.merge_records_high_water([new],[old],29,{partition:100},{partition:200}),[new])
        self.assertEqual(agent.merge_records_high_water([new],[old]),[new])
        old[3] = 10
        self.assertEqual(agent.merge_records_high_water([new],[old]),[old])

    def test_record_total_only_pair_and_cumulative_reset(self):
        p = agent.CodexUsageState("thread", 1794484800)
        p.on_line(frame("token_usage_record", {"response_id": "one", "usage": usage()}))
        p = agent.CodexUsageState("thread", 1794484800, meta=p.state(), events=list(p.rows.values()))
        p.on_line(count(None, usage()))
        p.on_line(frame("token_usage_record", {"response_id": "two", "usage": usage(60, 7, 10, 3)}, 2))
        p.on_line(count(None, usage(160, 27, 70, 13), 2))
        self.assertEqual(len(p.rows), 2)
        self.assertEqual(list(p.rows.values())[1][3:7], [47, 7, 10, 3])
        p = agent.CodexUsageState("thread", 1794484800)
        for second, values in enumerate([(100, 10, 20, 5), (20, 2, 5, 1), (30, 3, 7, 2), (20, 2, 5, 1), (20, 2, 5, 1)], 1):
            p.on_line(count(None, usage(*values), second))
        self.assertEqual(len(p.rows), 4)
        self.assertEqual(sum(sum(ev[3:7]) for ev in p.rows.values()), 165)

    def test_unreadable_store_and_failed_cursor_write_stop_without_erasing_history(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            path = home / ".toklens" / "ledger.db"
            path.parent.mkdir()
            damaged = b"deliberately invalid test ledger"
            path.write_bytes(damaged)
            with self.assertRaises(RuntimeError):
                agent.CursorStore(home)
            self.assertEqual(path.read_bytes(), damaged)
            with self.assertRaises(RuntimeError):
                agent.merge_with_ledger_db(home, [row("model", 30, 60)])
            self.assertEqual(path.read_bytes(), damaged)
            path.unlink()
            store = agent.CursorStore(home)
            store.put("test-cursor", str(home), 1, 10, 1, 10, {}, [])
            with mock.patch.object(agent, "_open_ledger_rw", side_effect=sqlite3.OperationalError("test failure")):
                with self.assertRaises(RuntimeError):
                    store.flush()
            self.assertEqual(len(store.dirty), 1)
            store.flush()
            self.assertEqual(len(store.dirty), 0)
            self.assertIsNotNone(agent.CursorStore(home).get("test-cursor"))

    def test_dual_record_incremental_compaction_and_cache_write(self):
        p = agent.CodexUsageState("thread", 1794484800, 480)
        p.on_line(frame("turn_context", {"model": "gpt-6.1-sol"}, 0))
        p.on_line(frame("token_usage_record", {"response_id": "one", "usage": usage()}))
        q = agent.CodexUsageState("thread", 1794484800, 480, p.state(), list(p.rows.values()))
        q.on_line(count(usage(), usage()))
        q.on_line(count(usage(), usage(), 2))
        self.assertEqual(len(q.rows), 1)
        self.assertEqual(list(q.rows.values())[0][3:7], [30, 20, 60, 10])
        q.on_line(frame("token_usage_record", {"response_id": "compact", "usage": usage(50, 5, 10, 0)}, 3))
        q.on_line(count(usage(0, 0, 0, 0), usage(150, 25, 70, 10), 3))
        self.assertEqual(len(q.rows), 2)

    def test_last_only_totals_only_and_independent_lanes(self):
        p = agent.CodexUsageState("thread", 1794484800)
        p.on_line(count(usage(), None))
        p.on_line(count(usage(), None))
        self.assertEqual(len(p.rows), 1)
        p = agent.CodexUsageState("thread", 1794484800)
        p.on_line(count(None, usage(100, 10, 20, 5)))
        p.on_line(count(None, usage(160, 17, 30, 8), 2))
        self.assertEqual(list(p.rows.values())[1][3:7], [47, 7, 10, 3])
        p = agent.CodexUsageState("thread", 1794484800)
        p.on_line(count(usage(10, 1, 0, 0), usage(1000, 100, 500, 0)))
        p.on_line(count(usage(20, 2, 0, 0), usage(20, 2, 0, 0), 2))
        self.assertEqual(len(p.rows), 2)

    def test_lower_correction_tombstone_and_whole_rows(self):
        part = "2026-11-12||OpenAI Codex"
        old, new = [row("wrong", 100, 900, 10)], [row("right", 30, 200, 3)]
        corrected = agent.merge_records_high_water(new, old, primary_partitions={part: 200})
        self.assertEqual(corrected, new)
        self.assertEqual(agent.merge_records_high_water(old, new, historical_partitions={part: 200}), new)
        self.assertEqual(agent.merge_records_high_water(corrected, corrected, primary_partitions={part: 200}, historical_partitions={part: 200}), new)
        self.assertEqual(agent.merge_records_high_water(corrected, [], primary_partitions={part: 200}, historical_partitions={part: 300}), [])
        merged = agent.merge_records_high_water([row("same", 200, 800, 10)], [row("same", 100, 900, 10)])
        self.assertEqual(sum(merged[0][4:8]), 1000)

    def test_native_step_mapping_checkpoint_and_unknown_model(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            root = home / ".gemini" / "antigravity"
            cid = "11111111-1111-1111-1111-111111111111"
            logs = root / "brain" / cid / ".system_generated" / "logs"
            logs.mkdir(parents=True)
            (root / "conversations").mkdir()
            conn = sqlite3.connect(root / "conversations" / (cid + ".db"))
            conn.execute("CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB)")
            conn.execute("CREATE TABLE steps(idx INTEGER PRIMARY KEY,step_type INTEGER,metadata BLOB)")
            for index, backend, tier in [(9, "gemini-4-carbon", "Gemini 4 Carbon (High)"), (0, "gemini-flash-worker-agy", "Gemini Flash")]:
                data = field(1, field(19, backend.encode()) + field(21, tier.encode()))
                conn.execute("INSERT INTO gen_metadata VALUES (?,?)", (index, data))
            conn.execute("INSERT INTO steps VALUES (101,15,?)", (field(20, varint(3 << 3) + varint(9)),))
            conn.execute("INSERT INTO steps VALUES (102,23,?)", (field(20, b""),))
            conn.commit()
            conn.close()
            entries = []
            for index, kind, status in [(101, "PLANNER_RESPONSE", "DONE"), (102, "CHECKPOINT", "DONE"), (103, "PLANNER_RESPONSE", "DONE"), (104, "PLANNER_RESPONSE", "PENDING"), (105, "USER_INPUT", "DONE")]:
                entries.append(dict(step_index=index, type=kind, status=status, created_at="2026-11-12T12:00:00Z", input_tokens=100, output_tokens=20, cache_read_tokens=60))
            text = "\n".join(map(json.dumps, entries)) + "\n"
            (logs / "transcript_full.jsonl").write_text(text)
            (logs / "transcript.jsonl").write_text(text)
            scanner = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False)
            scanner.scan_antigravity()
            scanner._finalize_request_events()
            rows = scanner.build_records()
            self.assertEqual(sum(r[3] for r in rows), 3)
            self.assertEqual({r[2] for r in rows}, {"gemini-4-carbon-high", "gemini-flash-worker-agy", "unknown"})
            self.assertEqual(next(r for r in rows if r[2] == "unknown")[8], 0)
            self.assertGreater(next(r for r in rows if r[2] == "gemini-flash-worker-agy")[8], 0)

    def test_file_copies_reconcile_and_repeated_scan(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            tid = "11111111-1111-1111-1111-111111111111"
            stream = "\n".join([frame("session_meta", {"id": tid}, 0), frame("turn_context", {"model": "gpt-6.1-sol"}, 0), frame("token_usage_record", {"response_id": "one", "usage": usage()}), count(usage(), usage())]) + "\n"
            for sub in ("sessions", "archived_sessions"):
                path = home / ".codex" / sub
                path.mkdir(parents=True)
                (path / (tid + ".jsonl")).write_text(stream)
            scanner = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False)
            rows = scanner.scan_all()
            self.assertEqual(sum(r[3] for r in rows), 1)
            self.assertEqual(sum(sum(r[4:8]) for r in rows), 120)
            proof = dict(scanner.reconciled_partitions)
            agent.merge_with_ledger_db(home, [row("wrong", 10000, 90000, 100)])
            scanner = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False)
            again = scanner.scan_all()
            self.assertEqual(rows, again)
            self.assertTrue(scanner.reconciled_partitions)
            live = home / ".codex" / "sessions" / (tid + ".jsonl")
            live.write_text(stream + frame("turn_context", {"model": "partial-model"}, 2) + "\n" + frame("token_usage_record", {"response_id": "partial", "usage": usage(400,20,0,0)}, 3) + '\n{"type":"token_usage_record",broken}\n')
            partial = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False)
            self.assertEqual(partial.scan_all(), rows)
            self.assertIn("OpenAI Codex", partial.failed_exact_tools)
            import os
            live.write_text(stream.replace("gpt-6.1-sol", "gpt-6.1-lux"))
            os.utime(live, ns=(live.stat().st_atime_ns, live.stat().st_mtime_ns + 1000000000))
            (home / ".codex" / "archived_sessions" / (tid + ".jsonl")).unlink()
            revised = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False).scan_all()
            self.assertEqual(len(revised), 1)
            self.assertEqual(revised[0][2], "gpt-6.1-lux")

    def test_unknown_rate_is_not_a_different_models_price(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            pricing = agent.PricingLookup(Path(temp), allow_network=False)
            self.assertEqual(pricing.lookup("gpt-nonexistent-price"), (0, 0, 0, 0))
            self.assertEqual(pricing.lookup("gemini-4-carbon-high"), pricing.lookup("gemini-4-carbon"))

    def test_verified_fork_replay_is_not_new_model_usage(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            path = home / ".codex" / "sessions"
            path.mkdir(parents=True)
            parent = "22222222-2222-2222-2222-222222222222"
            child = "33333333-3333-3333-3333-333333333333"
            inherited = count(usage(), usage())
            (path / (parent + ".jsonl")).write_text("\n".join([frame("session_meta", {"id": parent}, 0), frame("turn_context", {"model": "gpt-6.1-sol"}, 0), inherited]) + "\n")
            (path / (child + ".jsonl")).write_text("\n".join([frame("session_meta", {"id": child, "forked_from_id": parent}, 2), frame("turn_context", {"model": "gpt-6-astra"}, 0), inherited, count(usage(80, 10, 20, 0), usage(180, 30, 80, 10), 3)]) + "\n")
            rows = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False).scan_all()
            self.assertEqual(sum(r[3] for r in rows), 2)
            self.assertEqual(sum(sum(r[4:8]) for r in rows), 210)

    def test_compressed_response_records_and_exact_millisecond_time(self):
        try:
            from compression import zstd
        except ImportError:
            self.skipTest("Standard-library Zstandard is unavailable on this Python version")
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            tid = "44444444-4444-4444-4444-444444444444"
            path = home / ".codex" / "archived_sessions"
            path.mkdir(parents=True)
            stream = "\n".join([frame("session_meta", {"id": tid}, 0), frame("turn_context", {"model": "gpt-6.1-sol"}, 0), frame("token_usage_record", {"response_id": "compressed", "usage": usage()}), count(usage(), usage())]) + "\n"
            (path / (tid + ".jsonl.zst")).write_bytes(zstd.compress(stream.encode()))
            rows = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False).scan_all()
            self.assertEqual(sum(r[3] for r in rows), 1)
            self.assertEqual(sum(sum(r[4:8]) for r in rows), 120)
            self.assertEqual(agent._usage_epoch_ms("2026-11-12T12:00:00.779Z", 0) % 1000, 779)

    def test_existing_cloud_price_cache_and_custom_override(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            store = home / ".toklens"
            store.mkdir()
            (store / "pricing_cache.json").write_text(json.dumps({"models": {"gpt-5.2": [1.8, 14.1, 0.175, 0], "from-cache": [9, 18, 0.9, 0]}}))
            pricing = agent.PricingLookup(home, allow_network=False)
            self.assertEqual(pricing.lookup("from-cache"), (9, 18, 0.9, 0))
            self.assertEqual(pricing.lookup("gpt-5.2")[0], agent.BUILTIN_RATES["gpt-5.2"][0])
            (store / "custom_prices.json").write_text(json.dumps({"gpt-6.1-sol": {"input": 0.42, "output": 2, "cache_read": 0.1, "cache_write": 0}}))
            self.assertEqual(agent.PricingLookup(home, allow_network=False).lookup("gpt-6.1-sol"), (0.42, 2, 0.1, 0))

    def test_zcode_native_model_id_casing_and_no_invented_default(self):
        with tempfile.TemporaryDirectory(prefix="toklens-accounting-") as temp:
            home = Path(temp)
            path = home / ".zcode" / "cli" / "db"
            path.mkdir(parents=True)
            conn = sqlite3.connect(path / "db.sqlite")
            conn.execute("CREATE TABLE message(id TEXT,time_created INTEGER,data TEXT)")
            for index, model in enumerate(["glm-5.3-flash", None]):
                data = dict(role="assistant", providerId="openai", time={"created": 1794484800000}, tokens=dict(input=100, output=20, total=120, cache=dict(read=60, write=10)))
                if model is not None:
                    data["modelId"] = model
                conn.execute("INSERT INTO message VALUES (?,?,?)", (str(index), 1794484800000, json.dumps(data)))
            conn.commit()
            conn.close()
            scanner = agent.MultiHarnessScanner(home, tz_offset_min=480, allow_network_pricing=False)
            rows = scanner.scan_all()
            self.assertEqual({r[2] for r in rows}, {"glm-5.3-flash", "unknown"})
            self.assertEqual([sum(r[4:8]) for r in rows], [120, 120])
            self.assertEqual(len(scanner.reconciled_partitions), 1)


if __name__ == "__main__":
    unittest.main()
