using System;
using System.Collections.Generic;
using System.Linq;

namespace XkScreenshot.Translate;

/// <summary>
/// 按字符所属的文字系统判语种。
///
/// 不上统计式语种识别：截图里常常只有十几个字符，那个长度上统计法并不比看字形准，
/// 却要多带一份模型和一套词频表。文字系统本身就已经把大半语言分开了，剩下几个
/// 共用文字的（西里尔字母那一族、阿拉伯字母那一族）按常见程度排队 ——
/// 只在同一种文字内部消歧，不会跨文字乱认。
///
/// 拉丁字母是这套办法的盲区 —— 德法西葡意长得一样。默认按英语算：真要区分，
/// 得引入统计模型，那笔开销留给以后。汉字那边的简繁之分不在盲区里，
/// 见 <see cref="ChineseVariant"/>。
///
/// 独立于任何翻译引擎：在线引擎把翻译整个交给了大模型，答不上「你能翻成什么」，
/// 但界面上那句「检测到 X」两种模式都要显示，判语种这件事本身跟引擎无关。
/// </summary>
public static class ScriptLanguage
{
    /// <summary>
    /// 判语种，判不出返回 null（纯数字、纯符号会走到这儿）。
    ///
    /// <paramref name="prefer"/> 用来在同一种文字的几个候选之间挑（西里尔那一族、
    /// 通用字写成的中文）：离线引擎拿它筛「装了模型的」。**只在同种文字内部起作用** ——
    /// 一个候选都不满足时照实报第一个，绝不跨文字换一个满足条件的来充数，
    /// 那样界面就会把「认得出是韩文但没装韩语模型」说成「检测到中文」。
    /// 字形上已经能定死的（简繁就是）只给一个候选，<paramref name="prefer"/> 推不翻它。
    /// </summary>
    public static string? Detect(string text, Func<string, bool>? prefer = null)
    {
        var candidates = Candidates(text);
        if (candidates is null) return null;

        // 汉字区段只说明「这是中文」，简繁得再看用字
        if (candidates[0] == "zh") candidates = ChineseVariant(text) ?? candidates;

        return (prefer is null ? null : candidates.FirstOrDefault(prefer)) ?? candidates[0];
    }

    /// <summary>认出文字系统，给出这种文字下的候选语种（按常见程度排）。</summary>
    private static string[]? Candidates(string text)
    {
        int han = 0, kana = 0, hangul = 0, cyrillic = 0, arabic = 0, latin = 0;
        int hebrew = 0, greek = 0, thai = 0, devanagari = 0, bengali = 0;
        int tamil = 0, telugu = 0, kannada = 0, gujarati = 0, malayalam = 0;

        foreach (char ch in text)
        {
            // 区段用码位写，不用字面汉字：'鿿' 一眼能对着 Unicode 表核，'鿿' 不能
            if (ch is >= '぀' and <= 'ヿ') kana++;                     // 平假名 / 片假名
            else if (ch is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ') hangul++;
            else if (ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han++;
            else if (ch is >= 'Ѐ' and <= 'ӿ') cyrillic++;
            else if (ch is >= '֐' and <= '׿') hebrew++;
            else if (ch is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ') arabic++;
            else if (ch is >= 'Ͱ' and <= 'Ͽ') greek++;
            else if (ch is >= '฀' and <= '๿') thai++;
            else if (ch is >= 'ऀ' and <= 'ॿ') devanagari++;
            else if (ch is >= 'ঀ' and <= '৿') bengali++;
            else if (ch is >= '஀' and <= '௿') tamil++;
            else if (ch is >= 'ఀ' and <= '౿') telugu++;
            else if (ch is >= 'ಀ' and <= '೿') kannada++;
            else if (ch is >= '઀' and <= '૿') gujarati++;
            else if (ch is >= 'ഀ' and <= 'ൿ') malayalam++;
            else if (ch <= 'ɏ' && char.IsLetter(ch)) latin++;              // 含拉丁字母扩展 A/B
        }

        // 假名一出现就是日语：日文里汉字再多也一定夹着假名，反过来中文里一个假名都不会有。
        // 谚文同理，韩文里的汉字（汉字词）远少于谚文本身
        if (kana > 0) return ["ja"];
        if (hangul > 0 && hangul >= han) return ["ko"];

        // 剩下的按出现最多的那种文字来断，同一种文字内部按常见程度排队
        var byScript = new (int Count, string[] Candidates)[]
        {
            // 简繁在这一步分不开，交给 ChineseVariant 看用字
            (han,        ["zh", "zh_hant"]),
            (hangul,     ["ko"]),
            (cyrillic,   ["ru", "uk", "bg", "sr", "be"]),
            (arabic,     ["ar", "fa", "ur"]),
            (hebrew,     ["he"]),
            (greek,      ["el"]),
            (thai,       ["th"]),
            (devanagari, ["hi", "mr"]),
            (bengali,    ["bn"]),
            (tamil,      ["ta"]),
            (telugu,     ["te"]),
            (kannada,    ["kn"]),
            (gujarati,   ["gu"]),
            (malayalam,  ["ml"]),
            (latin,      ["en"]),
        };

        return byScript.Where(s => s.Count > 0)
            .OrderByDescending(s => s.Count)
            .Select(s => s.Candidates)
            .FirstOrDefault();
    }

    // ---------------- 简繁 ----------------

    /// <summary>
    /// 简繁哪一边。
    ///
    /// 汉字那个区段只说明「这是中文」：简繁在码位上是混着的，简体文本里也有大量
    /// 从未简化过的字，光看区段永远只能报简体。所以看的是「只在一边用得上的字」——
    /// 这种字一出现就是确定的证据，两边各数一遍谁多算谁。数而不是见一个就定，
    /// 是为了压住简繁混排的文本（简体文章里引了一段繁体，或者反过来）。
    ///
    /// 两边都没出现就返回 null 而不是硬猜一个：「我在北京」这种全用通用字的句子，
    /// 简繁写法完全一样，本来就分不出来 —— 那时候把选择交回调用方
    /// （离线引擎会挑装了模型的那个）比自己拍一个准。
    /// </summary>
    private static string[]? ChineseVariant(string text)
    {
        int simplified = 0, traditional = 0;

        foreach (char ch in text)
        {
            if (Simplified.Contains(ch)) simplified++;
            else if (Traditional.Contains(ch)) traditional++;
        }

        if (simplified == traditional) return null;
        return simplified > traditional ? ["zh"] : ["zh_hant"];
    }

    private static readonly HashSet<char> Simplified = [.. SimplifiedChars];
    private static readonly HashSet<char> Traditional = [.. TraditionalChars];

    /// <summary>
    /// 只在简体那边用得上的字。
    ///
    /// 和下面那张繁体表按同样的词序排，方便逐行对着核 —— 但代码只把两张表当集合用，
    /// 从不查「这个字对应那个字」，所以某一行多一个少一个都不会让判断出错，
    /// 只是那个字白填了。
    ///
    /// 只收「一边在用、另一边不用」的字。台、只、于、后、干、里、着、系 这种两边都在用的
    /// 一个都不能收：收了，繁体文本里每出现一个常用字就是一票投给简体。
    /// 也不求全 —— 几十个字的一段话总会撞上好几个，而越往生僻字里收，
    /// 撞上「其实两边都在用」的机会越大，判错的风险反而涨。
    /// </summary>
    private const string SimplifiedChars =
        "这说国会个们为来对时学发动过现实体机当点从" +
        "业开还间样与关内数气变没问无长见计车书东马" +
        "门华汉请电语认识边图处务头应义号张战报术权" +
        "极标检欢热环产确离种积简节药营虽装复观规觉" +
        "记讲许论设证评词试该误读课调谁谢让议讯访诉" +
        "负责货贵买费资赞贫贷贸赢贴贺赛贝贡财败赏赠" +
        "转轮软轻载达适选递邮银铁错队阳阴险难页项须" +
        "预领风飞饭馆验鸡齐儿亲爱写岁师帮归录怀态总" +
        "执扩担换据断旧显杂构树楼汇济测满灭灯烦爷盘" +
        "称稳穷竞笔苏补讨训译诗详谈轨辆迟遗郑邻铜锁" +
        "镇闭阅阶陆顶顺颜饮饰驻骑鲁鸣龄龙鱼鸟习乡决" +
        "听币忆敌择拟摄沟泽浅炉虫蚀谱输逻闲叶几么网" +
        "纪约级纯纲纳练组细织终绍经结绘给络绝统继续" +
        "绪缓编缘缝缩纸线绿缠紧类粮双医卫厂厅压历参" +
        "团园圆场坏块坚声壮备夺奋宝宪宾寻导层属岛峡" +
        "带广庄庆库弃弹彻径忧恋惊惧惨愿扑扫扬抢护拥" +
        "挥损摆杀条桥欧毕汤泪洁浆浓润渐湾灵炼烧牵犹" +
        "独狭猪献玛疗疯盐监睁瞒矿码碍礼窃竖签篮钉钓" +
        "钟钢钥钩钱钻铃铸铺链销锅锋锐锦键镜阀闪闯闷" +
        "闹闻阵际陈随隐颁颈频颗题额颤驰驱驳驾骂骄骗" +
        "鸥鸦鸭鸽鹅鹤鹰鸿鲜饥饱饲饼饿馒趋赵吗启丽严" +
        "单卖尽举乐争亚亿仅仓仪价众优伟传伤伦侧侨债" +
        "倾偿储剧剑劝办协势陕区农运进远连迁违罗联职" +
        "脑艺获蓝辞辩辽针铝顾顿颠飘驼骤麦齿龟胆脏肠" +
        "腊舰荣萧蜡袜贤阔";

    /// <summary>只在繁体那边用得上的字。词序跟上面那张表一样。</summary>
    private const string TraditionalChars =
        "這說國會個們為來對時學發動過現實體機當點從" +
        "業開還間樣與關內數氣變沒問無長見計車書東馬" +
        "門華漢請電語認識邊圖處務頭應義號張戰報術權" +
        "極標檢歡熱環產確離種積簡節藥營雖裝複觀規覺" +
        "記講許論設證評詞試該誤讀課調誰謝讓議訊訪訴" +
        "負責貨貴買費資贊貧貸貿贏貼賀賽貝貢財敗賞贈" +
        "轉輪軟輕載達適選遞郵銀鐵錯隊陽陰險難頁項須" +
        "預領風飛飯館驗雞齊兒親愛寫歲師幫歸錄懷態總" +
        "執擴擔換據斷舊顯雜構樹樓匯濟測滿滅燈煩爺盤" +
        "稱穩窮競筆蘇補討訓譯詩詳談軌輛遲遺鄭鄰銅鎖" +
        "鎮閉閱階陸頂順顏飲飾駐騎魯鳴齡龍魚鳥習鄉決" +
        "聽幣憶敵擇擬攝溝澤淺爐蟲蝕譜輸邏閒葉幾麼網" +
        "紀約級純綱納練組細織終紹經結繪給絡絕統繼續" +
        "緒緩編緣縫縮紙線綠纏緊類糧雙醫衛廠廳壓歷參" +
        "團園圓場壞塊堅聲壯備奪奮寶憲賓尋導層屬島峽" +
        "帶廣莊慶庫棄彈徹徑憂戀驚懼慘願撲掃揚搶護擁" +
        "揮損擺殺條橋歐畢湯淚潔漿濃潤漸灣靈煉燒牽猶" +
        "獨狹豬獻瑪療瘋鹽監睜瞞礦碼礙禮竊豎簽籃釘釣" +
        "鐘鋼鑰鉤錢鑽鈴鑄鋪鏈銷鍋鋒銳錦鍵鏡閥閃闖悶" +
        "鬧聞陣際陳隨隱頒頸頻顆題額顫馳驅駁駕罵驕騙" +
        "鷗鴉鴨鴿鵝鶴鷹鴻鮮飢飽飼餅餓饅趨趙嗎啟麗嚴" +
        "單賣盡舉樂爭亞億僅倉儀價眾優偉傳傷倫側僑債" +
        "傾償儲劇劍勸辦協勢陝區農運進遠連遷違羅聯職" +
        "腦藝獲藍辭辯遼針鋁顧頓顛飄駝驟麥齒龜膽臟腸" +
        "臘艦榮蕭蠟襪賢闊";
}
