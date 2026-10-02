using System;
using System.Collections.Generic;
using System.Linq;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>站点类型定义</summary>
public sealed record SiteTypeInfo(string Name, string Glyph, string Color);

/// <summary>
/// 站点分类器：优先用域名关键词匹配，其次用页面标题/描述兜底。
/// 纯本地规则，不依赖外部服务。
/// </summary>
public sealed class SiteClassifier
{
    public static readonly SiteTypeInfo Unknown = new("未知", "❔", "#8F98A0");

    public static readonly IReadOnlyList<SiteTypeInfo> AllTypes = new[]
    {
        new SiteTypeInfo("开发技术", "⌨", "#4FC3F7"),
        new SiteTypeInfo("AI 工具", "✨", "#B388FF"),
        new SiteTypeInfo("设计素材", "🎨", "#FF8A65"),
        new SiteTypeInfo("视频影音", "▶", "#FF5252"),
        new SiteTypeInfo("社交社区", "💬", "#4DD0E1"),
        new SiteTypeInfo("购物电商", "🛒", "#FFB74D"),
        new SiteTypeInfo("资讯阅读", "📰", "#90A4AE"),
        new SiteTypeInfo("学习教育", "📚", "#81C784"),
        new SiteTypeInfo("游戏娱乐", "🎮", "#7E57C2"),
        new SiteTypeInfo("效率工具", "⚙", "#64B5F6"),
        new SiteTypeInfo("金融理财", "💰", "#FFD54F"),
        new SiteTypeInfo("网盘存储", "☁", "#4DB6AC"),
        new SiteTypeInfo("邮箱办公", "✉", "#7986CB"),
        new SiteTypeInfo("政府机构", "🏛", "#A1887F"),
        new SiteTypeInfo("下载资源", "⇩", "#AED581")
    };

    private static SiteTypeInfo Find(string name) =>
        AllTypes.FirstOrDefault(t => t.Name == name) ?? Unknown;

    /// <summary>域名关键词 → 类型。命中即返回，按声明顺序优先。</summary>
    private static readonly (string Type, string[] Keys)[] DomainRules =
    {
        ("开发技术", new[] { "github", "gitlab", "gitee", "stackoverflow", "stackexchange", "npmjs", "nuget",
            "crates.io", "pypi", "maven", "docker", "hub.docker", "vercel", "netlify", "cloudflare", "azure",
            "aws.amazon", "aliyun", "tencentcloud", "huaweicloud", "jetbrains", "visualstudio", "vscode",
            "csdn", "cnblogs", "juejin", "segmentfault", "oschina", "51cto", "infoq", "zhihu.com/org",
            "w3school", "runoob", "developer.mozilla", "mdn", "devdocs", "readthedocs", "atlassian",
            "sourceforge", "apache.org", "gnu.org", "kernel.org", "sqlite.org", "postgresql", "mysql",
            "redis.io", "mongodb", "elastic.co", "kubernetes", "golang.org", "rust-lang", "python.org",
            "nodejs.org", "typescriptlang", "dart.dev", "flutter", "unity.com", "unrealengine", "godotengine",
            "openai", "anthropic", "huggingface", "kaggle", "colab.research", "leetcode", "nowcoder",
            "docs.google", "wireshark", "virtualbox", "vmware", "portswigger", "exploit-db" }),

        ("AI 工具", new[] { "chatgpt", "claude.ai", "gemini.google", "bard.google", "copilot.microsoft",
            "perplexity", "poe.com", "midjourney", "stability.ai", "leonardo.ai", "runwayml",
            "kimi.moonshot", "moonshot.cn", "yiyan.baidu", "tongyi.aliyun", "qianwen", "doubao.com",
            "chatglm", "zhipuai", "bigmodel.cn", "deepseek", "minimax", "baichuan-ai", "01.ai",
            "suno.com", "elevenlabs", "jasper.ai", "copy.ai", "notion.so/ai", "cursor.com", "cursor.sh",
            "codeium", "tabnine", "replit", "civitai", "openart.ai", "tusiart", "liblib" }),

        ("设计素材", new[] { "dribbble", "behance", "figma", "sketch.com", "adobe", "canva", "unsplash",
            "pexels", "pixabay", "freepik", "iconfont", "flaticon", "iconfinder", "thenounproject",
            "fonts.google", "google.com/fonts", "dafont", "zcool", "huaban", "ui.cn", "uisdc",
            "material.io", "lottiefiles", "coolors", "colorhunt", "remove.bg", "tinypng", "photopea",
            "blender.org", "autodesk", "3d66", "aigei", "ziticq", "tukuppt", "pngtree" }),

        ("视频影音", new[] { "bilibili", "youtube", "youku", "iqiyi", "v.qq", "qq.com/x/cover", "mgtv",
            "douyin", "tiktok", "kuaishou", "huya", "douyu", "acfun", "missevan", "netease.com/music",
            "music.163", "spotify", "soundcloud", "xiami", "kugou", "kuwo", "qiyi", "le.com",
            "pornhub", "netflix", "disneyplus", "hbo", "imdb", "douban.com/movie", "pixiv",
            "hanime", "anime", "dmzj", "manhuagui", "mangadex", "wg04", "agedm", "gimy" }),

        ("社交社区", new[] { "weibo", "zhihu.com", "douban", "tieba.baidu", "reddit", "twitter",
            "x.com", "facebook", "instagram", "telegram", "t.me", "discord", "whatsapp", "wechat",
            "qq.com", "xiaohongshu", "xhslink", "jike", "v2ex", "hostloc", "linux.do", "nga",
            "hupu", "maimai", "pincong", "youtube.com/channel", "linkedin", "mastodon", "tumblr",
            "ycombinator", "news.ycombinator", "producthunt", "bbs", "forum" }),

        ("购物电商", new[] { "taobao", "tmall", "jd.com", "pinduoduo", "yangkeduo", "suning", "vip.com",
            "amazon", "ebay", "aliexpress", "alibaba", "1688", "dhgate", "temu", "shein", "shopee",
            "lazada", "walmart", "target.com", "bestbuy", "microsoft.com/store", "apple.com/shop",
            "xiaomiyoupin", "smzdm", "mogujie", "kaola", "sephora", "nike", "adidas", "uniqlo",
            "dangdang", "book.douban", "kongfz" }),

        ("资讯阅读", new[] { "news.", "toutiao", "ifeng", "sina.com", "163.com", "sohu", "thepaper",
            "chinanews", "people.com", "xinhuanet", "cctv", "bbc", "cnn", "reuters", "nytimes",
            "theguardian", "wsj", "bloomberg", "ft.com", "economist", "wikipedia", "wiki", "baike",
            "zh.wikipedia", "medium.com", "substack", "36kr", "huxiu", "tmtpost", "geekpark",
            "sspai", "appinn", "ithome", "cnbeta", "solidot", "zaobao" }),

        ("学习教育", new[] { "coursera", "edx", "udemy", "khanacademy", "udacity", "codecademy",
            "freecodecamp", "w3school", "imooc", "icourse163", "xuetangx", "bilibili.com/cheese",
            "udemy.com", "duolingo", "youdao", "iciba", "hujiang", "open.163", "ted.com",
            "scholar.google", "researchgate", "arxiv", "sciencedirect", "springer", "ieee",
            "nature.com", "science.org", "cnki", "wanfangdata", "zotero", "libgen" }),

        ("游戏娱乐", new[] { "steam", "steampowered", "epicgames", "gog.com", "uplay", "ubisoft",
            "blizzard", "battle.net", "origin.com", "ea.com", "playstation", "xbox", "nintendo",
            "nexusmods", "3dmgame", "ali213", "gamersky", "ign.com", "gamersky", "vgtime",
            "op.gg", "u.gg", "leagueoflegends", "chess.com", "lichess", "4399", "7k7k",
            "taptap", "gcores", "indienova", "minecraft", "roblox" }),

        ("效率工具", new[] { "notion.so", "evernote", "yuque", "wolai", "flowus", "feishu.cn",
            "larksuite", "dingtalk", "slack.com", "teams.microsoft", "trello", "asana", "monday.com",
            "clickup", "jira", "linear.app", "obsidian.md", "logseq", "typora", "xmind",
            "processon", "draw.io", "excalidraw", "miro", "figma.com/board", "zapier", "ifttt",
            "pastebin", "tinypaste", "wetransfer", "smallpdf", "ilovepdf", "onetab" }),

        ("金融理财", new[] { "alipay", "paypal", "stripe", "visa", "mastercard", "unionpay",
            "icbc", "ccb.com", "abchina", "boc.cn", "cmbchina", "bankofchina", "psbc", "cebbank",
            "eastmoney", "xueqiu", "10jqka", "jrj.com", "hexun", "bloomberg.com/markets",
            "coinmarketcap", "coingecko", "binance", "okx", "huobi", "coinbase", "tradingview",
            "chase.com", "boc", "hsbc", "citi.com" }),

        ("网盘存储", new[] { "pan.baidu", "aliyundrive", "alipan", "115.com", "weiyun", "cloud.189",
            "123pan", "lanzou", "lanzoui", "cowtransfer", "dropbox", "drive.google", "onedrive",
            "mega.nz", "mediafire", "box.com", "pcloud", "terabox", "quark.cn", "drive.uc.cn",
            "jianguoyun", "nutstore" }),

        ("邮箱办公", new[] { "mail.", "gmail", "outlook", "hotmail", "163.com/mail", "mail.qq",
            "126.com", "yeah.net", "foxmail", "zoho.com/mail", "protonmail", "proton.me",
            "office.com", "microsoft365", "docs.google", "sheets.google", "slides.google",
            "officeapps", "wps.cn", "kdocs", "金山文档" }),

        ("政府机构", new[] { ".gov.cn", "gov.uk", "gov.hk", "gov.tw", "gov.us", "europa.eu",
            "un.org", "who.int", "imf.org", "worldbank", "chinatax", "12333.gov", "12345" }),

        ("下载资源", new[] { "pan.", "dl.", "download", "xiazai", "soft.", "pc6.com", "cr173",
            "onlinedown", "duote", "pconline", "zol.com", "ithome.com/link", "mydown",
            "piracy", "torrent", "1337x", "rarbg", "nyaa", "fitgirl", "dodi-repacks",
            "github.com/releases", "sourceforge.net/projects", "filecr", "getintopc" })
    };

    /// <summary>页面标题/描述兜底关键词 → 类型</summary>
    private static readonly (string Type, string[] Keys)[] TextRules =
    {
        ("开发技术", new[] { "开发者", "文档", "api", "sdk", "教程", "代码", "编程", "开源",
            "documentation", "developer", "repository", "framework" }),
        ("AI 工具", new[] { "人工智能", "大模型", "ai ", "aigc", "智能助手", "ai工具" }),
        ("视频影音", new[] { "视频", "在线观看", "影视", "动漫", "直播", "音乐", "video", "watch" }),
        ("购物电商", new[] { "商城", "购物", "旗舰店", "优惠券", "商品", "shop", "store", "buy" }),
        ("资讯阅读", new[] { "新闻", "资讯", "日报", "博客", "专栏", "news", "blog", "magazine" }),
        ("学习教育", new[] { "课程", "学习", "公开课", "题库", "考试", "教育", "course", "learn", "university" }),
        ("游戏娱乐", new[] { "游戏", "攻略", "手游", "网游", "game", "gaming", "wiki" }),
        ("社交社区", new[] { "论坛", "社区", "问答", "贴吧", "forum", "community", "social" }),
        ("网盘存储", new[] { "网盘", "云盘", "云存储", "分享文件", "cloud drive", "netdisk" }),
        ("金融理财", new[] { "银行", "证券", "基金", "理财", "支付", "保险", "bank", "invest" })
    };

    public SiteTypeInfo Classify(string url, string? pageTitle = null, string? hintText = null)
    {
        var domain = UrlHelper.GetDomain(url).ToLowerInvariant();
        var full = (url ?? string.Empty).ToLowerInvariant();
        var type = MatchDomain(domain, full);
        if (type != null) return Find(type);

        var text = ((pageTitle ?? string.Empty) + " " + (hintText ?? string.Empty)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var (name, keys) in TextRules)
                if (keys.Any(k => text.Contains(k, StringComparison.Ordinal)))
                    return Find(name);
        }

        return Unknown;
    }

    private static string? MatchDomain(string domain, string fullUrl)
    {
        foreach (var (type, keys) in DomainRules)
            foreach (var key in keys)
            {
                // 含点或斜杠的规则（如 "v.qq"、"163.com/mail"）需要带上路径一起匹配，
                // 纯单词规则（如 "github"、"forum"）只匹配域名，避免路径里出现同名词造成误判
                var target = key.Contains('.') || key.Contains('/') ? fullUrl : domain;
                if (target.Contains(key, StringComparison.Ordinal)) return type;
            }

        // TLD 兜底
        if (domain.EndsWith(".gov.cn", StringComparison.Ordinal) ||
            domain.EndsWith(".gov", StringComparison.Ordinal)) return "政府机构";
        if (domain.EndsWith(".edu.cn", StringComparison.Ordinal) ||
            domain.EndsWith(".edu", StringComparison.Ordinal)) return "学习教育";

        return null;
    }
}
