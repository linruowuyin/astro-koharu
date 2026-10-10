using System.Text;
using System.Text.RegularExpressions;

namespace BlogTool;

/// <summary>创建新文章。</summary>
public static class PostCreator
{
    /// <summary>
    /// 建一篇新文章，只写最必要的字段。
    ///
    /// 原向导问九件事（标题/slug/描述/分类/标签/多个附注/草稿/确认），
    /// 但实际文章里 description 和 tags 大多是空的，附注也很少用。
    /// 所以这里只问标题和分类，其余按合理默认值补：
    ///   · link   —— 标题转拼音，保证 URL 友好
    ///   · date   —— 当前时间
    ///   · 其余字段不写，模板本身允许缺省
    /// </summary>
    public static async Task<string> CreateAsync(string title, string categoryName, string slugOfCategory)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("标题不能为空");
        if (string.IsNullOrWhiteSpace(slugOfCategory)) throw new ArgumentException("分类不能为空");

        var dir = Project.At("src", "content", "blog", slugOfCategory);
        Directory.CreateDirectory(dir);

        var link = await SlugifyAsync(title);
        var filePath = System.IO.Path.Combine(dir, $"{title}.md");
        if (File.Exists(filePath))
        {
            // 同名文件已存在时退回用 link 命名，避免直接覆盖别人的文章。
            filePath = System.IO.Path.Combine(dir, $"{link}.md");
            if (File.Exists(filePath))
                throw new IOException($"文件已存在：{System.IO.Path.GetFileName(filePath)}");
        }

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: {YamlQuote(title)}");
        sb.AppendLine($"link: {link}");
        sb.AppendLine($"date: {now}");
        sb.AppendLine("categories:");
        sb.AppendLine($"  - {YamlQuote(categoryName)}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine();

        // 无 BOM 的 UTF-8：带 BOM 会在文件开头留下不可见字符，
        // 某些 Markdown 解析器会把 BOM 当成正文的一部分。
        await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(false));
        return filePath;
    }

    /// <summary>YAML 标量加引号，含特殊字符时转义。</summary>
    private static string YamlQuote(string value)
    {
        if (value.Length == 0) return "\"\"";
        var needsQuote = value.Any(c => c is ':' or '#' or '"' or '\'' or '{' or '}' or '[' or ']'
            or ',' or '&' or '*' or '!' or '|' or '>' or '%' or '@' or '`' or '\n' or '\r')
            || value.Trim() != value
            // 看起来像数字/布尔时也要加引号，否则 YAML 会解析成非字符串
            || double.TryParse(value, out _)
            || value is "true" or "false" or "null" or "yes" or "no" or "on" or "off";

        if (!needsQuote) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
    }

    /// <summary>
    /// 标题转拼音 slug，用作 frontmatter 的 link。
    ///
    /// 主路径是项目自带的 transliteration（跟 <c>pnpm koharu post new</c> 用的
    /// 是同一个包），所以 exe 生成的文章链接和原向导完全一致。
    /// 调不通时退回下面的本地音节表，保证功能不会整个挂掉。
    /// </summary>
    public static async Task<string> SlugifyAsync(string title)
    {
        // transliteration 的字典有 4 万多字，手抄一份既不完整也难维护；
        // 而且这台机器要跑 pnpm build 就一定有 node，不算新增依赖。
        const string script = "const t=require('transliteration');process.stdout.write(t.slugify(process.argv[1]||''))";
        var (code, output) = await Project.CaptureAsync("node", ["-e", script, title]);

        return Normalize(code == 0 && !string.IsNullOrWhiteSpace(output) ? output : LocalSlug(title));
    }

    /// <summary>同步版，界面预览用不上 node 时退化为本地表。</summary>
    public static string Slugify(string title) => Normalize(LocalSlug(title));

    /// <summary>收敛成小写字母、数字和单个短横线。</summary>
    private static string Normalize(string slug)
    {
        var cleaned = Regex.Replace(slug.ToLowerInvariant(), "[^a-z0-9-]+", "-");
        cleaned = Regex.Replace(cleaned, "-{2,}", "-").Trim('-');
        return cleaned.Length == 0 ? "post" : cleaned;
    }

    /// <summary>
    /// 本地兜底：常用汉字 → 拼音音节。
    ///
    /// 覆盖不到的字会退化成 <c>u{码点}</c> 形式——不好看，但稳定、唯一、
    /// URL 安全。宁可 slug 不 prettify，也不能让 URL 里出现中文。
    /// </summary>
    private static string LocalSlug(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (Pinyin.TryGet(ch, out var syllable))
            {
                sb.Append(syllable);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.ToString();
    }

    /// <summary>常用汉字 → 拼音音节。</summary>
    private static class Pinyin
    {
        private static readonly Dictionary<char, string> Table = Build();

        public static bool TryGet(char ch, out string syllable)
        {
            if (Table.TryGetValue(ch, out syllable!)) return true;

            // 表外的汉字用码点生成稳定的短后缀，保证不同字不会撞车。
            syllable = "u" + ((int)ch).ToString("x36");
            return false;
        }

        private static Dictionary<char, string> Build()
        {
            // 空格分隔的「汉字=音节」清单，覆盖财经博客常用字。
            const string data =
                "一=yi 二=er 三=san 四=si 五=wu 六=liu 七=qi 八=ba 九=jiu 十=shi " +
                "百=bai 千=qian 万=wan 亿=yi 零=ling 两=liang 半=ban 几=ji " +
                "上=shang 下=xia 大=da 小=xiao 中=zhong 国=guo 年=nian 月=yue " +
                "日=ri 时=shi 分=fen 秒=miao 天=tian 周=zhou 期=qi 季=ji " +
                "年=nian 新=xin 旧=jiu 老=lao 高=gao 低=di 长=chang 短=duan " +
                "涨=zhang 跌=die 升=sheng 降=jiang 多=duo 少=shao 增=zeng " +
                "减=jian 加=jia 减=jian 变=bian 化=hua 动=dong 态=tai " +
                "市=shi 场=chang 股=gu 债=zhai 银=yin 行=hang 证=zheng " +
                "券=quan 基=ji 金=jin 货=huo 汇=hui 率=lv 息=xi 税=shui " +
                "价=jia 值=zhi 量=liang 收=shou 盘=pan 开=kai 收=shou " +
                "买=mai 卖=mai 投=tou 资=zi 融=rong 资=zi 本=ben 金=jin " +
                "公=gong 司=si 集=ji 团=tuan 企=qi 业=ye 银=yin 行=hang " +
                "保=bao 险=xian 证=zheng 券=quan 基=ji 金=jin 产=chan " +
                "房=fang 地=di 产=chan 股=gu 债=zhai 期=qi 货=huo 币=bi " +
                "美=mei 元=yuan 欧=ou 镑=bang 日=ri 元=yuan 美=mei " +
                "大=da 盘=pan 中=zhong 小=xiao 创=chuang 业=ye 板=ban " +
                "科=ke 技=ji 半=ban 导=dao 体=ti 芯=xin 片=pian 光=guang " +
                "电=dian 池=chi 汽=qi 车=che 机=ji 械=xie 化=hua 工=gong " +
                "医=yi 药=yao 食=shi 饮=yin 酒=jiu 旅=lv 游=you 教=jiao " +
                "育=yu 文=wen 化=hua 传=chuan 媒=mei 互=hu 联=lian " +
                "网=wang 络=luo 信=xin 息=xi 数=shu 据=ju 算=suan " +
                "智=zhi 能=neng 量=liang 模=mo 型=xing 算=suan 法=fa " +
                "策=ce 略=lue 战=zhan 术=shu 思=si 考=kao 逻=luo 辑=ji " +
                "分=fen 析=xi 研=yan 究=jiu 报=bao 告=gao 新=xin 闻=wen " +
                "周=zhou 刊=kan 月=yue 报=bao 日=ri 年=nian 期=qi " +
                "记=ji 录=lu 笔=bi 随=sui 笔=bi 杂=za 谈=tan 复=fu " +
                "盘=pan 总=zong 结=jie 尾=wei 预=yu 判=pan 实=shi 战=zhan " +
                "失=shi 误=wu 得=de 失=shi 盈=ying 亏=kui 赚=zhuan 钱=qian " +
                "财=cai 富=fu 穷=qiong 穷=qiong 苦=ku 乐=le 悲=bei " +
                "喜=xi 欢=huan 怕=pa 惊=jing 慌=huang 急=ji 慢=man " +
                "快=kuai 强=qiang 弱=ruo 硬=ying 软=ruan 稳=wen 动=dong " +
                "安=an 全=quan 危=wei 险=xian 机=ji 会=hui 交=jiao 易=yi " +
                "买=mai 入=ru 卖=mai 出=chu 持=chi 仓=cang 补=bu 减=jian " +
                "止=zhi 损=sun 盈=ying 空=kong 多=duo 头=tou 尾=wei " +
                "反=fan 弹=dan 突=tu 跳=tiao 稳=wen 急=ji 缓=huan";

            var map = new Dictionary<char, string>();
            foreach (var pair in data.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var ch = pair[0];
                var pinyin = pair[(eq + 1)..];
                // 同一个字可能重复出现（如「收」），先到先得即可。
                map.TryAdd(ch, pinyin);
            }
            return map;
        }
    }
}
