using System;
using System.IO;
using TokLens;

internal static class PricingAliasTests
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "TokLens_PricingAliases_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".toklens"));
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "store"));
        try
        {
            PricingEngine prices = new PricingEngine(root);
            foreach (string suffix in new string[] { "auto", "max", "fast", "ultrafast", "online", "nitro", "exacto", "1m" })
            {
                string model = "gpt-6.1-sol-" + suffix;
                Check(prices.Lookup(model).Source == "unpriced", model + " cannot borrow the base model price");
                UsageEvent ev = new UsageEvent("OpenAI Codex", model, "2026-11-17", 1000000, 100, 10, 0, 0, 1);
                ev.Provider = "openai";
                double cost, baseline; ModelRate rate;
                prices.ComputeEventCost(ev, out cost, out baseline, out rate);
                Check(ev.CostBasis == "unpriced" && cost == 0 && ev.FreshInput == 1000000,
                    model + " preserves usage and exposes its missing cost");
            }
            File.WriteAllText(Path.Combine(root, ".toklens", "custom_prices.json"),
                "{\"gpt-6.1-sol-auto\":{\"input\":7,\"output\":21,\"cache_read\":1,\"cache_write\":0}," +
                "\"gpt-6.1-sol-max\":{\"input\":0,\"output\":0,\"cache_read\":0,\"cache_write\":0}}");
            prices = new PricingEngine(root);
            Check(prices.Lookup("gpt-6.1-sol-auto").InputPerM == 7 && prices.Lookup("gpt-6.1-sol-auto").Source == "custom",
                "an exact variant quote remains usable");
            Check(prices.Lookup("gpt-6.1-sol-max").Source == "custom" && prices.Lookup("gpt-6.1-sol-max").InputPerM == 0,
                "an explicit free quote is distinct from an unknown price");
            UsageEvent free = new UsageEvent("OpenAI Codex", "gpt-6.1-sol-max", "2026-11-17", 1000000, 100, 0, 0, 0, 1);
            double freeCost, freeBaseline; ModelRate freeRate;
            prices.ComputeEventCost(free, out freeCost, out freeBaseline, out freeRate);
            Check(freeCost == 0 && free.CostBasis != "unpriced" && freeRate.Source == "custom",
                "an explicitly free model remains priced in event totals");
            foreach (bool zero in new bool[] { false, true })
            {
                File.WriteAllText(Path.Combine(root, ".toklens", "custom_prices.json"), zero
                    ? "{\"gpt-6.1-sol\":{\"input\":0,\"output\":0,\"cache_read\":0,\"cache_write\":0}}"
                    : "{\"gpt-6.1-sol\":{\"input\":7,\"output\":21,\"cache_read\":1,\"cache_write\":0}}");
                prices = new PricingEngine(root);
                foreach (string tier in new string[] { "unknown", "standard" })
                {
                    UsageEvent custom = new UsageEvent("OpenAI Codex", "gpt-6.1-sol", "2026-11-17", 1000000, 100, 0, 0, 0, 1);
                    custom.ActualServiceTier = tier;
                    prices.ComputeEventCost(custom, out freeCost, out freeBaseline, out freeRate);
                    Check(Math.Abs(freeCost - (zero ? 0 : 7.0021)) < 0.00000001 && freeRate.Source == "custom" && custom.CostBasis != "unpriced",
                        tier + " preserves explicit custom " + (zero ? "free" : "paid") + " quote over the bundled standard rate");
                    custom.ActualServiceTier = "fast";
                    prices.ComputeEventCost(custom, out freeCost, out freeBaseline, out freeRate);
                    Check(Math.Abs(freeCost - 8.003) < 0.00000001 && freeRate.Source != "custom" && custom.CostBasis == "estimated_tier_price",
                        "a custom standard quote cannot replace the separately known long-context Fast quote");
                    custom.ActualServiceTier = "flex";
                    prices.ComputeEventCost(custom, out freeCost, out freeBaseline, out freeRate);
                    Check(custom.CostBasis == "unpriced", "a missing Flex quote cannot borrow a custom standard quote");
                    custom.LoggedCostUsd = 3.25;
                    prices.ComputeEventCost(custom, out freeCost, out freeBaseline, out freeRate);
                    Check(freeCost == 3.25 && custom.CostBasis == "reported", "a reported charge keeps precedence over custom prices");
                }
            }
            Console.WriteLine("PASS: " + checks + " pricing alias assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", null); }
    }
}
