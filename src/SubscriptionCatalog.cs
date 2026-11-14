using System;
using System.Collections.Generic;
using System.Globalization;

namespace TokLens
{
    // Catalogue prices are reference prices, never evidence that a user bought a plan.
    public sealed class SubscriptionOffer
    {
        public string Id, ProviderId, Name, Currency, Region, SourceUrl, Note;
        public double? Amount;
        public int Months;
        public const string VerifiedOn = "2026-11-14";
        public override string ToString()
        {
            return Name + " · " + (Amount.HasValue ? SubscriptionCatalog.Money(Amount.Value, Currency) + "/" + (Months == 12 ? "年" : Months == 3 ? "季" : "月") : "按账单填写") + " · " + Region;
        }
    }

    public sealed class SubscriptionProvider
    {
        public string Id, Name, Group, SourceUrl;
        public override string ToString() { return Name; }
    }

    public static class SubscriptionCatalog
    {
        public static readonly SubscriptionProvider[] Providers = new SubscriptionProvider[] {
            Provider("google", "Google AI", "海外", "https://gemini.google/us/subscriptions/?hl=en"),
            Provider("claude", "Claude", "海外", "https://claude.com/pricing"),
            Provider("openai", "ChatGPT / Codex", "海外", "https://learn.chatgpt.com/docs/pricing"),
            Provider("glm", "智谱 GLM / ZCode", "国产", "https://www.bigmodel.cn/glm-coding"),
            Provider("kimi", "Kimi", "国产", "https://www.kimi.com/help/membership/membership-pricing"),
            Provider("aliyun", "阿里云百炼", "国产", "https://help.aliyun.com/zh/model-studio/token-plan-personal-overview"),
            Provider("minimax", "MiniMax", "国产", "https://www.minimax.cn/m-plan"),
            Provider("volcano", "火山方舟", "国产", "https://www.volcengine.com/docs/82379/1925114?lang=zh"),
            Provider("tencent", "腾讯云 · 通用 Token Plan", "国产", "https://cloud.tencent.com/document/product/1823/130060/"),
            Provider("tencent-hy", "腾讯云 · Hy Token Plan", "国产", "https://cloud.tencent.com/act/pro/tokenplan"),
            Provider("codebuddy", "CodeBuddy 中国版", "国产", "https://www.codebuddy.cn/pricing/"),
            Provider("baidu", "百度千帆", "国产", "https://cloud.baidu.com/doc/qianfan/s/Dmrabu8b6"),
            Provider("deepseek", "DeepSeek", "国产", "https://platform.deepseek.com/"),
            Provider("cursor", "Cursor", "工具", "https://cursor.com/pricing"),
            Provider("windsurf", "Devin / Windsurf", "工具", "https://devin.ai/pricing"),
            Provider("copilot", "GitHub Copilot", "工具", "https://github.com/features/copilot/plans"),
            Provider("trae", "Trae", "工具", "https://www.trae.ai/pricing"),
            Provider("zed", "Zed", "工具", "https://zed.dev/pricing"),
            Provider("other", "其他服务商 / 自定义", "自定义", ""),
            Provider("coding_plan", "旧版国产合并项（待拆分）", "旧记录", "")
        };
        public static readonly List<SubscriptionOffer> Offers = CreateOffers();

        private static SubscriptionProvider Provider(string id, string name, string group, string url)
        { return new SubscriptionProvider { Id=id, Name=name, Group=group, SourceUrl=url }; }
        public static SubscriptionProvider FindProvider(string id)
        { foreach (SubscriptionProvider p in Providers) if (p.Id == id) return p; foreach(SubscriptionProvider p in Providers)if(p.Id=="other")return p; throw new InvalidOperationException("Missing custom provider"); }
        private static void Add(List<SubscriptionOffer> list, string provider, string id, string name, double? amount, string currency, int months, string region, string note)
        { list.Add(new SubscriptionOffer { ProviderId=provider, Id=id, Name=name, Amount=amount, Currency=currency, Months=months, Region=region, Note=note, SourceUrl=FindProvider(provider).SourceUrl }); }
        private static List<SubscriptionOffer> CreateOffers()
        {
            List<SubscriptionOffer> a = new List<SubscriptionOffer>();
            Add(a,"google","google-plus","AI Plus",4.99,"USD",1,"美国","不等同于 Antigravity 额度；以账户权益为准");
            Add(a,"google","google-pro","AI Pro",19.99,"USD",1,"美国","");
            Add(a,"google","google-ultra-5","AI Ultra 5x",99.99,"USD",1,"美国","");
            Add(a,"google","google-ultra-20","AI Ultra 20x",199.99,"USD",1,"美国","");
            Add(a,"claude","claude-pro","Pro",20,"USD",1,"美国","");
            Add(a,"claude","claude-pro-year","Pro 年付",200,"USD",12,"美国","全年一次扣款；月均为 200 ÷ 12");
            Add(a,"claude","claude-max","Max",100,"USD",1,"美国","");
            Add(a,"claude","claude-ultra","Ultra",200,"USD",1,"美国","旧套餐按原始账单登记，不能自动套用新名称");
            Add(a,"openai","chatgpt-free","Free",0,"USD",1,"美国","");
            Add(a,"openai","chatgpt-go","Go",8,"USD",1,"美国","");
            Add(a,"openai","chatgpt-plus","Plus",20,"USD",1,"美国","");
            Add(a,"openai","chatgpt-pro-100","Pro $100",100,"USD",1,"美国","");
            Add(a,"openai","chatgpt-pro-200","Pro $200",200,"USD",1,"美国","");
            Add(a,"openai","chatgpt-pro-500","Pro $500",500,"USD",1,"美国","");
            string[] glm={"Lite","Pro","Max"}; double[] gp={118,538,1078};
            for(int i=0;i<glm.Length;i++) {
                Add(a,"glm","glm-"+glm[i].ToLowerInvariant(),"GLM Coding Plan "+glm[i],gp[i],"CNY",1,"中国大陆","连续包月目录价；优惠按实付修改");
                Add(a,"glm","glm-"+glm[i].ToLowerInvariant()+"-quarter","GLM "+glm[i]+" 季付",Math.Round(gp[i]*.8*3,2),"CNY",3,"中国大陆","按官方折合月价 × 3 计算整季总额");
                Add(a,"glm","glm-"+glm[i].ToLowerInvariant()+"-year","GLM "+glm[i]+" 年付",Math.Round(gp[i]*.7*12,2),"CNY",12,"中国大陆","按官方折合月价 × 12 计算全年总额");
            }
            string[] kn={"Go","Plus","Pro","Max","Ultra"}; double[] km={49,99,199,699,1399}, ky={468,948,1908,6708,13428};
            for(int i=0;i<kn.Length;i++) {
                string note=i==0?"Go 不包含 Kimi Code":"包含 Kimi Code；额度以账户为准";
                Add(a,"kimi","kimi-"+kn[i].ToLowerInvariant(),kn[i],km[i],"CNY",1,"中国大陆",note);
                Add(a,"kimi","kimi-"+kn[i].ToLowerInvariant()+"-year",kn[i]+" 年付",ky[i],"CNY",12,"中国大陆",note);
            }
            string[] an={"Lite","Essential","Standard","Pro"}; double[] ap={39,79,139,312};
            for(int i=0;i<an.Length;i++) Add(a,"aliyun","aliyun-"+an[i].ToLowerInvariant(),"Token Plan "+an[i],ap[i],"CNY",1,"中国大陆","目录价。Lite/Essential 新客限时价 19.5/39.5 元，截至 2026-11-30；按实际付款登记");
            string[] mn={"Go","Explore","Build"}; double[] mm={49,119,469}, my={490,1190,4690};
            for(int i=0;i<mn.Length;i++) {
                Add(a,"minimax","minimax-"+mn[i].ToLowerInvariant(),"M Plan "+mn[i],mm[i],"CNY",1,"中国大陆","");
                Add(a,"minimax","minimax-"+mn[i].ToLowerInvariant()+"-year","M Plan "+mn[i]+" 年付",my[i],"CNY",12,"中国大陆","全年一次扣款");
            }
            string[] tn={"Lite","Standard","Pro","Max"}; double[] tp={39,99,299,599}, th={28,78,238,468};
            for(int i=0;i<tn.Length;i++) {
                Add(a,"tencent","tencent-"+tn[i].ToLowerInvariant(),"通用 Token Plan "+tn[i],tp[i],"CNY",1,"中国大陆","积分套餐；不能将原始 Token 总量直接当作套餐额度消耗");
                Add(a,"tencent-hy","tencent-hy-"+tn[i].ToLowerInvariant(),"Hy Token Plan "+tn[i],th[i],"CNY",1,"中国大陆","与通用 Token Plan 可分别订阅，分别登记");
            }
            string[] bn={"Mini","Lite","Standard","Pro","Max"};double[] bp={9.9,39,100,200,600};
            for(int i=0;i<bn.Length;i++)Add(a,"baidu","baidu-"+bn[i].ToLowerInvariant(),"Token Plan "+bn[i],bp[i],"CNY",1,"中国大陆","常规目录价。双十一首购 / 续费与 Mini 限时优惠另计；按订单实付填写，不把活动价当长期续费价");
            string[] cbn={"标准版","高级版","旗舰版"};double[] cbm={99,199,999},cbr={70,140,700};
            for(int i=0;i<cbn.Length;i++) {
                Add(a,"codebuddy","codebuddy-"+i+"-month",cbn[i]+" 单月",cbm[i],"CNY",1,"中国大陆","单月购买，不自动续订；与腾讯云 Token Plan 是不同产品");
                Add(a,"codebuddy","codebuddy-"+i+"-renew",cbn[i]+" 连续包月",cbr[i],"CNY",1,"中国大陆","自动续订优惠；关闭自动续订后的价格按订单核对");
            }
            Add(a,"codebuddy","codebuddy-one","Buddy One VIP 连续包月",700,"CNY",1,"中国大陆","推广期与旗舰版同价；升级后原旗舰版取消，不能重复登记");
            Add(a,"codebuddy","codebuddy-one-year","Buddy One VIP 连续包年",6720,"CNY",12,"中国大陆","推广期价格；全年一次扣款，升级替代旗舰版");
            Add(a,"volcano","volcano-lite","Coding Plan Lite",null,"CNY",1,"中国大陆","官网动态报价未返回可核实金额；请按实际订单登记");
            Add(a,"volcano","volcano-pro","Coding Plan Pro",null,"CNY",1,"中国大陆","官网动态报价未返回可核实金额；请按实际订单登记");
            Add(a,"cursor","cursor-plus","Plus",8,"USD",1,"美国","仅 Grok 模型；第三方模型费用须另行确认");
            Add(a,"cursor","cursor-premium","Premium",30,"USD",1,"美国","第三方模型按量另计");
            Add(a,"cursor","cursor-super","Super",100,"USD",1,"美国","第三方模型按量另计");
            Add(a,"cursor","cursor-ultra","Ultra",200,"USD",1,"美国","第三方模型按量另计");
            Add(a,"windsurf","devin-pro","Devin Pro",20,"USD",1,"美国","Windsurf 旧订阅按旧账单填写");
            Add(a,"windsurf","devin-max","Devin Max",200,"USD",1,"美国","");
            Add(a,"copilot","copilot-free","Free",0,"USD",1,"美元定价","");
            Add(a,"copilot","copilot-pro","Pro",10,"USD",1,"美元定价","");
            Add(a,"copilot","copilot-pro-plus","Pro+",39,"USD",1,"美元定价","");
            Add(a,"copilot","copilot-max","Max",100,"USD",1,"美元定价","");
            Add(a,"trae","trae-free","Free",0,"USD",1,"国际版","");
            Add(a,"trae","trae-pro","Pro",20,"USD",1,"国际版","月付；年付按订单总额登记");
            Add(a,"trae","trae-pro-plus","Pro+",60,"USD",1,"国际版","月付；年付按订单总额登记");
            Add(a,"trae","trae-ultra","Ultra",200,"USD",1,"国际版","月付；年付按订单总额登记");
            Add(a,"zed","zed-personal","Personal",0,"USD",1,"美元定价","自带 API Key 或外部代理的费用另计");
            Add(a,"zed","zed-pro","Pro",10,"USD",1,"美元定价","包含 $5 托管模型用量，超出部分另计");
            foreach(SubscriptionProvider p in Providers)
                Add(a,p.Id,p.Id+"-custom","自定义 / 历史 / 团队套餐",null,p.Group=="国产"?"CNY":"USD",1,"按账单","未核实的报价不预填；可登记其他地区、团队席位和历史套餐");
            return a;
        }
        public static string Money(double amount, string currency)
        { return (currency=="CNY"?"¥":currency=="USD"?"$":currency+" ") + amount.ToString("N2",CultureInfo.InvariantCulture); }
        public static bool ValidAmount(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value>=0 && value<=100000000; }
        private static string Field(IDictionary<string,object> d,string key)
        { object v; return d.TryGetValue(key,out v) && v!=null ? Convert.ToString(v,CultureInfo.InvariantCulture) : ""; }
        public static SubscriptionPlanConfig ReadConfig(IDictionary<string,object> d)
        {
            SubscriptionPlanConfig s=new SubscriptionPlanConfig(); bool flag; double amount; int months;
            s.EcoId=Field(d,"eco_id"); s.PlanName=Field(d,"plan_name");
            s.Enabled=bool.TryParse(Field(d,"enabled"),out flag)&&flag;
            if(double.TryParse(Field(d,"monthly_usd"),NumberStyles.Float,CultureInfo.InvariantCulture,out amount)&&ValidAmount(amount)) s.MonthlyUsd=amount;
            s.Id=Field(d,"id"); s.OfferId=Field(d,"offer_id"); s.Currency=Field(d,"currency");
            if(string.IsNullOrEmpty(s.Currency)) s.Currency="USD";
            bool amountValid=double.TryParse(Field(d,"amount"),NumberStyles.Float,CultureInfo.InvariantCulture,out amount)&&ValidAmount(amount);
            s.Amount=amountValid?amount:0;
            bool monthsValid=int.TryParse(Field(d,"billing_months"),out months)&&months>0&&months<=120;
            s.BillingMonths=monthsValid?months:1;
            s.Region=Field(d,"region"); s.AccountLabel=Field(d,"account_label"); s.StartDate=Field(d,"start_date"); s.EndDate=Field(d,"end_date"); s.Note=Field(d,"note");
            s.FeeConfirmed=bool.TryParse(Field(d,"fee_confirmed"),out flag)&&flag && amountValid && monthsValid && (s.Currency=="CNY"||s.Currency=="USD");
            return s;
        }
        public static Dictionary<string,object> WriteConfig(SubscriptionPlanConfig s)
        {
            Dictionary<string,object> d=new Dictionary<string,object>();
            d["eco_id"]=s.EcoId; d["enabled"]=s.Enabled; d["plan_name"]=s.PlanName; d["monthly_usd"]=ValidAmount(s.MonthlyUsd)?s.MonthlyUsd:0;
            d["id"]=s.Id; d["offer_id"]=s.OfferId; d["currency"]=s.Currency; d["amount"]=ValidAmount(s.Amount)?Math.Round(s.Amount,4):0;
            d["billing_months"]=s.BillingMonths; d["region"]=s.Region; d["account_label"]=s.AccountLabel; d["start_date"]=s.StartDate; d["end_date"]=s.EndDate; d["note"]=s.Note;
            d["fee_confirmed"]=s.FeeConfirmed && ValidAmount(s.Amount);
            return d;
        }
        public static bool IsActive(SubscriptionPlanConfig s, DateTime date)
        {
            DateTime start,end;
            return s!=null && s.Enabled && s.FeeConfirmed && ValidAmount(s.Amount) && (s.Currency=="CNY"||s.Currency=="USD") && s.BillingMonths>0 && s.BillingMonths<=120 &&
                DateTime.TryParseExact(s.StartDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out start) &&
                DateTime.TryParseExact(s.EndDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out end) && start<=date.Date && date.Date<=end && s.EcoId!="coding_plan";
        }
        public static double MonthlyUsd(SubscriptionPlanConfig s,double rate)
        {
            if(s==null || !ValidAmount(s.Amount) || s.BillingMonths<=0) return 0;
            if(s.Currency=="USD") return s.Amount/s.BillingMonths;
            if(s.Currency=="CNY" && rate>0 && !double.IsNaN(rate) && !double.IsInfinity(rate)) return s.Amount/s.BillingMonths/rate;
            return 0;
        }
        public static bool Overlaps(SubscriptionPlanConfig a,SubscriptionPlanConfig b)
        {
            if(a==null || b==null || !a.Enabled || !b.Enabled || !a.FeeConfirmed || !b.FeeConfirmed || a.EcoId!=b.EcoId ||
                !string.Equals((a.AccountLabel??"").Trim(),(b.AccountLabel??"").Trim(),StringComparison.OrdinalIgnoreCase)) return false;
            DateTime a0,a1,b0,b1;
            return DateTime.TryParseExact(a.StartDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out a0) &&
                DateTime.TryParseExact(a.EndDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out a1) &&
                DateTime.TryParseExact(b.StartDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out b0) &&
                DateTime.TryParseExact(b.EndDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out b1) && a0<=b1 && b0<=a1;
        }
        // This describes model origin only. It deliberately does not guess the reseller,
        // account, subscription, or billing channel from the client application's name.
        public static string ModelOrigin(string model)
        {
            string m=(model??"").ToLowerInvariant();
            if(m.Contains("glm") || m.StartsWith("z-ai/")) return "智谱 GLM";
            if(m.Contains("kimi") || m.Contains("moonshot")) return "Kimi / Moonshot";
            if(m.Contains("qwen") || m.Contains("qvq") || m.Contains("tongyi")) return "通义 Qwen";
            if(m.Contains("minimax")) return "MiniMax";
            if(m.Contains("deepseek")) return "DeepSeek";
            if(m.Contains("doubao") || m.StartsWith("seed-")) return "字节豆包";
            if(m.Contains("hunyuan")) return "腾讯混元";
            if(m.Contains("ernie")) return "百度文心";
            if(m.Contains("mimo")) return "小米 MiMo";
            if(m.Contains("step-")) return "阶跃星辰";
            if(m.Contains("claude")) return "Anthropic";
            if(m.Contains("gemini") || m.Contains("gemma")) return "Google";
            if(m.Contains("gpt-") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.Contains("codex")) return "OpenAI";
            if(m.Contains("grok")) return "xAI";
            return "来源待识别";
        }
    }
}
