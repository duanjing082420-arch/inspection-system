// RecordPanelBuilder.cs
//
// 一键生成「巡检记录」面板（列表 + 右侧详情 + 刷新 + 空状态/错误提示 + 左上角入口按钮）。
//
// 菜单：Tools ▸ 巡检 ▸ 创建巡检记录面板 RecordPanel
// 重复执行会先删掉旧的，不会重影。
//
// 结构（Canvas 下）：
//   RecordPanel                  面板本体（初始隐藏）
//     ├ Header / CloseButton
//     ├ FilterPanel              筛选区（任务四）
//     │   ├ SearchInput / SearchButton / ResetFilterButton
//     │   └ DeviceDropdown / PriorityDropdown / StatusDropdown
//     ├ ListView(ScrollRect) → Viewport → Content（VerticalLayoutGroup）
//     │                          └ RecordItemTemplate（隐藏，运行时实例化）
//     ├ EmptyText                空状态 / 加载失败提示
//     ├ DetailTitle / DetailMeta / DetailDesc
//     ├ StatusOpenButton / StatusProgressButton / StatusResolvedButton（状态操作）
//     └ RefreshButton / StatusText / CountText
//   OpenRecordButton             左上角常驻入口
//   InspectionRecordList         控制器（常驻可见，不随面板隐藏）
//
// 控制器为什么单独一个物体：面板初始是隐藏的，组件挂在隐藏物体上 Awake 不会执行，
// 按钮事件就绑不上，表现就是「点按钮没反应」。上报面板踩过这个坑，这里从一开始就避开。
//
// 本文件必须放在 Assets/Editor 下。

using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class RecordPanelBuilder
{
    // 面板高度从 660 提到 780：多出来的 120px 就是顶部筛选区。
    // 不要靠压缩列表高度去腾地方 —— 列表本来就没几行可见，再压就没法用了。
    const float PANEL_W = 1040f;
    const float PANEL_H = 780f;

    const string FONT_PATH = "Assets/Fonts/InspectionYaHei SDF.asset";
    const string SPRITE_PANEL = "Assets/UI/RoundedRect_18.png";
    const string SPRITE_FIELD = "Assets/UI/RoundedRect_12.png";
    const string SPRITE_ITEM = "Assets/UI/RoundedRect_8.png";
    const string SPRITE_DOT = "Assets/UI/RoundedRect_9.png";
    const string SPRITE_ARROW = "Assets/UI/ArrowDown.png";

    const string PANEL_NAME = "RecordPanel";
    const string OPEN_BUTTON_NAME = "OpenRecordButton";
    const string CONTROLLER_NAME = "InspectionRecordList";

    static TMP_FontAsset font;
    static Sprite spritePanel;
    static Sprite spriteField;
    static Sprite spriteItem;
    static Sprite spriteDot;
    static Sprite spriteArrow;

    [MenuItem("Tools/巡检/创建巡检记录面板 RecordPanel", false, 30)]
    static void Build()
    {
        Canvas canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[巡检] 场景里没有 Canvas，请先执行 Tools ▸ 巡检 ▸ 创建上报面板 IssueFormPanel。");
            return;
        }

        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FONT_PATH);
        if (font == null)
        {
            font = TMP_Settings.defaultFontAsset;
            Debug.LogWarning("[巡检] 没找到 " + FONT_PATH + "，改用 TMP 默认字体（中文可能显示异常）。");
        }

        spritePanel = AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_PANEL);
        spriteField = AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_FIELD);
        spriteItem = AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_ITEM);
        spriteDot = AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_DOT);
        spriteArrow = AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_ARROW);

        // 幂等：先清掉旧的，避免重影和重复控制器
        DestroyExisting(canvas.transform, PANEL_NAME);
        DestroyExisting(canvas.transform, OPEN_BUTTON_NAME);
        DestroyExisting(canvas.transform, CONTROLLER_NAME);

        PanelParts parts;
        GameObject panel = BuildPanel(canvas.transform, out parts);
        Button openButton = BuildOpenButton(canvas.transform);

        // 上报模式横幅挂在 Canvas 下、面板外：进上报模式时面板要收起，
        // 提示得留在屏幕上，否则用户不知道"现在点场景会放标记"。
        parts.modeBanner = BuildModeBanner(canvas.transform);

        InspectionRecordList controller = Wire(canvas.transform, panel, openButton, parts);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[巡检] 巡检记录面板已创建并保存。\n" +
                  "  入口：屏幕左上角「巡检记录」按钮\n" +
                  "  筛选：顶部关键词搜索 + 设备/优先级/状态下拉 + 重置筛选\n" +
                  "  列表：左侧，点条目看右侧详情\n" +
                  "  控制器：" + controller.gameObject.name +
                  "（apiClient=" + (controller.apiClient != null ? "已接线" : "运行时自动查找") + "）\n" +
                  "  场景：" + scene.path);
    }

    static void DestroyExisting(Transform parent, string name)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
                Object.DestroyImmediate(child.gameObject);
        }
    }

    // ---------------- 面板 ----------------

    static GameObject BuildPanel(Transform canvas, out PanelParts parts)
    {
        parts = new PanelParts();

        GameObject panel = new GameObject(PANEL_NAME);
        RectTransform rt = AddRect(panel, canvas, new Vector2(PANEL_W, PANEL_H), Vector2.zero);
        SetImage(panel, spritePanel, new Color(0.97f, 0.97f, 0.98f, 1f));

        float left = -250f;   // 列表区中心 X
        float right = 330f;   // 详情区中心 X

        // 纵向排布（面板半高 390，可用 -376..374）：
        //   标题 348 / 筛选区 252（占 194..310）/ 列表 -64（占 -309..181）/ 底部工具条 -348
        //   每行之间留 8~14px，别让控件贴边。

        // 标题
        Text("Header", panel.transform, "巡检记录", 34, FontStyles.Bold,
            new Vector2(400, 52), new Vector2(-270, 348), TextAlignmentOptions.MidlineLeft,
            new Color(0.13f, 0.13f, 0.15f));

        Button closeButton = Button("CloseButton", panel.transform, "×", 40,
            new Vector2(64, 64), new Vector2(470, 348),
            new Color(0.86f, 0.30f, 0.28f));

        // 「＋ 新增问题」：进入上报模式（点完它再点场景才会放标记）。
        // 放在标题和关闭按钮之间的空白处，x 150..350，和左侧标题(到 -70)、右侧关闭(438 起) 都不碰。
        Button reportModeButton = Button("ReportModeButton", panel.transform, "＋ 新增问题", 24,
            new Vector2(200, 52), new Vector2(250, 348),
            new Color(0.20f, 0.55f, 0.86f));

        // 顶部筛选区（任务四）
        FilterParts filter = BuildFilterPanel(panel.transform);

        // 左侧：列表（ScrollRect）
        GameObject listView = new GameObject("ListView");
        AddRect(listView, panel.transform, new Vector2(560, 490), new Vector2(left, -64));
        SetImage(listView, spriteField, new Color(0.91f, 0.92f, 0.94f, 1f));

        GameObject viewport = new GameObject("Viewport");
        AddRect(viewport, listView.transform, Vector2.zero, Vector2.zero);
        Stretch(viewport.GetComponent<RectTransform>());

        // 裁剪必须用 RectMask2D，不能用 Mask：
        // Mask 靠「遮罩图自己的 alpha」往模板缓冲（stencil）里写值，
        // 而我们之前给 Viewport 挂的是一张 alpha=0 的透明图 —— alpha 过不了裁剪阈值，
        // 模板缓冲里一个像素都没写，于是 Viewport 里所有子物体（列表项）被整片裁掉。
        // 表现出来就是：数据加载成功、右侧详情正常、行对象也创建了，但左侧一片空白。
        // RectMask2D 只按矩形裁剪，不依赖任何 Graphic，也没有 stencil 开销。
        RectMask2D rectMask = viewport.AddComponent<RectMask2D>();
        rectMask.padding = Vector4.zero;

        GameObject content = new GameObject("Content");
        RectTransform contentRT = content.AddComponent<RectTransform>();
        contentRT.SetParent(viewport.transform, false);
        contentRT.anchorMin = new Vector2(0f, 1f);
        contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.pivot = new Vector2(0.5f, 1f);
        contentRT.sizeDelta = new Vector2(0f, 0f);
        contentRT.anchoredPosition = Vector2.zero;

        VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8f;
        layout.padding = new RectOffset(10, 10, 10, 10);
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = listView.AddComponent<ScrollRect>();
        scroll.content = contentRT;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        // 列表项模板（隐藏，运行时实例化）
        // 注意：label 传空串时 Button() 不会建 Text 子物体，而控制器要靠
        // GetComponentInChildren<TMP_Text> 找它，所以这里必须显式建一个。
        // Button() 返回的是 Button 组件，不是 GameObject，取 .gameObject 才能拿到物体
        Button templateButton = Button("RecordItemTemplate", content.transform, "", 20,
            new Vector2(0f, 76f), Vector2.zero, ColorNormal());
        GameObject template = templateButton.gameObject;

        Text("Text", template.transform, "", 22, FontStyles.Normal,
            new Vector2(500f, 70f), Vector2.zero, TextAlignmentOptions.MidlineLeft,
            new Color(0.15f, 0.15f, 0.18f));

        template.SetActive(false);

        // 空状态 / 错误提示（默认隐藏）
        GameObject empty = new GameObject("EmptyText");
        AddRect(empty, panel.transform, new Vector2(500, 260), new Vector2(left, -64));
        TMP_Text emptyText = empty.AddComponent<TextMeshProUGUI>();
        StyleText(emptyText, "", 24, FontStyles.Normal, TextAlignmentOptions.Center,
            new Color(0.45f, 0.45f, 0.48f));
        empty.SetActive(false);

        // 右侧：详情区
        GameObject detailBg = new GameObject("DetailArea");
        AddRect(detailBg, panel.transform, new Vector2(360, 490), new Vector2(right, -64));
        SetImage(detailBg, spriteField, new Color(1f, 1f, 1f, 1f));

        TMP_Text detailTitle = Text("DetailTitle", detailBg.transform, "", 26, FontStyles.Bold,
            new Vector2(330, 44), new Vector2(0, 205), TextAlignmentOptions.MidlineLeft,
            new Color(0.13f, 0.13f, 0.15f));

        // 详情区高度 490（下边 -245，上边 +245）。给底部状态按钮让出位置后重新排一遍：
        //   标题 183..227 / 元信息 -65..165 / 描述 -190..-90 / 状态按钮 -239..-191
        // 详情区多了一行「记录编号」，元信息框从 230 加高到 250 并上移 5，
        // 否则 8 行文字会压到下面的描述区（两者之间原本只剩 25px 空隙）。
        TMP_Text detailMeta = Text("DetailMeta", detailBg.transform, "", 22, FontStyles.Normal,
            new Vector2(330, 250), new Vector2(0, 55), TextAlignmentOptions.TopLeft,
            new Color(0.25f, 0.25f, 0.28f));

        TMP_Text detailDesc = Text("DetailDesc", detailBg.transform, "", 22, FontStyles.Normal,
            new Vector2(330, 100), new Vector2(0, -140), TextAlignmentOptions.TopLeft,
            new Color(0.35f, 0.35f, 0.38f));

        // 状态操作按钮（任务四）：三个状态对应后端 open / in_progress / resolved。
        // 做三个而不是两个，是为了状态能来回改（已解决也能退回处理中），
        // 演示时不必删数据重来。没有选中记录时控制器会把它们整体置灰。
        Button statusOpenButton = Button("StatusOpenButton", detailBg.transform, "待处理", 21,
            new Vector2(106, 48), new Vector2(-115, -215), new Color(0.55f, 0.58f, 0.62f));

        Button statusProgressButton = Button("StatusProgressButton", detailBg.transform, "处理中", 21,
            new Vector2(106, 48), new Vector2(0, -215), new Color(0.95f, 0.62f, 0.20f));

        Button statusResolvedButton = Button("StatusResolvedButton", detailBg.transform, "已解决", 21,
            new Vector2(106, 48), new Vector2(115, -215), new Color(0.20f, 0.62f, 0.35f));

        // 底部操作条。坐标以面板中心为原点，面板 1040x780 → 可用半宽 520、半高 390。
        // 加删除按钮后整条重排过，四格互不重叠：
        //   刷新   -475..-325（左边距 45）
        //   删除   -320..-190
        //   状态文字 -135..165
        //   计数文字 260..500（右边距 20）
        // 计数那格留宽一点：筛选后要显示「共找到 3 / 17 条记录」，比原来「共 17 条」长。
        Button refreshButton = Button("RefreshButton", panel.transform, "刷新", 26,
            new Vector2(150, 56), new Vector2(-400, -348), new Color(0.20f, 0.55f, 0.86f));

        Button deleteButton = Button("DeleteButton", panel.transform, "删除记录", 24,
            new Vector2(130, 56), new Vector2(-255, -348), new Color(0.83f, 0.25f, 0.22f));

        TMP_Text statusText = Text("StatusText", panel.transform, "", 21, FontStyles.Normal,
            new Vector2(300, 48), new Vector2(15, -348), TextAlignmentOptions.MidlineLeft,
            new Color(0.09f, 0.41f, 0.67f));

        TMP_Text countText = Text("CountText", panel.transform, "", 21, FontStyles.Normal,
            new Vector2(240, 48), new Vector2(380, -348), TextAlignmentOptions.MidlineRight,
            new Color(0.35f, 0.35f, 0.38f));

        // 面板默认隐藏，由左上角按钮打开
        panel.SetActive(false);

        parts.closeButton = closeButton;
        parts.refreshButton = refreshButton;
        parts.deleteButton = deleteButton;
        parts.emptyText = emptyText;
        parts.detailTitle = detailTitle;
        parts.detailMeta = detailMeta;
        parts.detailDesc = detailDesc;
        parts.statusText = statusText;
        parts.countText = countText;
        parts.statusOpenButton = statusOpenButton;
        parts.statusProgressButton = statusProgressButton;
        parts.statusResolvedButton = statusResolvedButton;
        parts.content = content.transform;
        parts.template = template;

        parts.searchInput = filter.searchInput;
        parts.searchButton = filter.searchButton;
        parts.deviceDropdown = filter.deviceDropdown;
        parts.priorityDropdown = filter.priorityDropdown;
        parts.statusDropdown = filter.statusDropdown;
        parts.resetFilterButton = filter.resetFilterButton;

        parts.reportModeButton = reportModeButton;

        // 把筛选区排到最后（同层级里画在最上面）。
        // 下拉展开的「Dropdown List」其实有 TMP 自己挂的 overrideSorting Canvas（sortingOrder 30000），
        // 不受这里的同级顺序影响；提到最上层只是让筛选区整体压住相邻的列表/详情背景，
        // 免得高亮、边框这类贴边的东西被邻居盖住一角。
        if (filter.root != null) filter.root.transform.SetAsLastSibling();

        return panel;
    }

    // ---------------- 筛选区（任务四） ----------------
    //
    //  两行排布，控件全部按面板中心为原点定位，横向安全区 -490..490：
    //   第一行（y=+26）：关键词 [输入框] [搜索]          提示文字
    //   第二行（y=-28）：设备[下拉] 优先级[下拉] 状态[下拉] [重置筛选]
    //
    //  下拉只在这里建好「控件 + 默认选项」，具体选项由控制器在运行时填：
    //  设备下拉要等数据回来才知道有哪些设备，写死就没法跟着数据走。

    static FilterParts BuildFilterPanel(Transform panel)
    {
        FilterParts filter = new FilterParts();

        GameObject root = new GameObject("FilterPanel");
        AddRect(root, panel, new Vector2(1000f, 116f), new Vector2(0f, 252f));
        SetImage(root, spriteField, new Color(0.93f, 0.94f, 0.96f, 1f));
        filter.root = root;

        Text("KeywordLabel", root.transform, "关键词", 21, FontStyles.Normal,
            new Vector2(80, 40), new Vector2(-450, 26), TextAlignmentOptions.MidlineLeft,
            new Color(0.25f, 0.25f, 0.28f));

        filter.searchInput = InputField("SearchInput", root.transform, "输入设备名或问题标题……",
            new Vector2(440, 46), new Vector2(-170, 26));

        filter.searchButton = Button("SearchButton", root.transform, "搜索", 22,
            new Vector2(130, 46), new Vector2(140, 26), new Color(0.20f, 0.55f, 0.86f));

        // 提示文字：宽度卡在 280，太长会折行挤到相邻控件上
        Text("SearchHint", root.transform, "支持：设备名/编号/标题/描述", 17, FontStyles.Normal,
            new Vector2(280, 40), new Vector2(350, 26), TextAlignmentOptions.MidlineLeft,
            new Color(0.50f, 0.50f, 0.53f));

        Text("DeviceLabel", root.transform, "设备", 21, FontStyles.Normal,
            new Vector2(52, 40), new Vector2(-464, -28), TextAlignmentOptions.MidlineLeft,
            new Color(0.25f, 0.25f, 0.28f));

        // 设备下拉：先只放「全部设备」，真选项由 RebuildDeviceOptions 按数据补
        filter.deviceDropdown = Dropdown("DeviceDropdown", root.transform,
            new List<string> { "全部设备" }, new Vector2(226, 42), new Vector2(-325, -28));

        Text("PriorityLabel", root.transform, "优先级", 21, FontStyles.Normal,
            new Vector2(72, 40), new Vector2(-176, -28), TextAlignmentOptions.MidlineLeft,
            new Color(0.25f, 0.25f, 0.28f));

        filter.priorityDropdown = Dropdown("PriorityDropdown", root.transform,
            new List<string> { "全部优先级", "高优先级", "中优先级", "低优先级" },
            new Vector2(176, 42), new Vector2(-52, -28));

        Text("StatusLabel", root.transform, "状态", 21, FontStyles.Normal,
            new Vector2(52, 40), new Vector2(62, -28), TextAlignmentOptions.MidlineLeft,
            new Color(0.25f, 0.25f, 0.28f));

        filter.statusDropdown = Dropdown("StatusDropdown", root.transform,
            new List<string> { "全部状态", "待处理", "处理中", "已解决" },
            new Vector2(176, 42), new Vector2(176, -28));

        filter.resetFilterButton = Button("ResetFilterButton", root.transform, "重置筛选", 22,
            new Vector2(140, 42), new Vector2(420, -28), new Color(0.62f, 0.63f, 0.66f));

        return filter;
    }

    // ---------------- 左上角入口按钮 ----------------

    static Button BuildOpenButton(Transform canvas)
    {
        GameObject go = new GameObject(OPEN_BUTTON_NAME);
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.SetParent(canvas, false);

        // 左上角锚定：不随面板尺寸变化跑位
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(180f, 66f);
        rt.anchoredPosition = new Vector2(24f, -24f);

        SetImage(go, spriteField, new Color(0.20f, 0.55f, 0.86f, 1f));
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();

        GameObject label = new GameObject("Text");
        AddRect(label, go.transform, new Vector2(180f, 66f), Vector2.zero);
        TMP_Text t = label.AddComponent<TextMeshProUGUI>();
        StyleText(t, "巡检记录", 26, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);

        return button;
    }

    /// <summary>
    /// 上报模式横幅。挂在 Canvas 下（不是面板里），因为进上报模式时面板要收起。
    /// 初始隐藏，由 InspectionRecordList.SetBanner 控制显隐。
    /// </summary>
    static TMP_Text BuildModeBanner(Transform canvas)
    {
        DestroyExisting(canvas, "ReportModeBanner");

        GameObject go = new GameObject("ReportModeBanner");

        // 画布 1920x1080、中心锚点：y=452 落在顶部，避开面板（面板上边只到 390）
        AddRect(go, canvas, new Vector2(780f, 64f), new Vector2(0f, 452f));
        SetImage(go, spriteField, new Color(0.98f, 0.62f, 0.15f, 0.97f));

        TMP_Text label = Text("Label", go.transform, "上报模式：点击设备放置问题标记　（按 Esc 退出）",
            24, FontStyles.Bold, new Vector2(740f, 56f), Vector2.zero,
            TextAlignmentOptions.Center, Color.white);

        // 默认隐藏：只有进上报模式才出现
        go.SetActive(false);

        return label;
    }

    // ---------------- 接线 ----------------

    static InspectionRecordList Wire(Transform canvas, GameObject panel, Button openButton, PanelParts parts)
    {
        GameObject go = new GameObject(CONTROLLER_NAME);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(canvas, false);

        InspectionRecordList list = go.AddComponent<InspectionRecordList>();

        list.panelRoot = panel;
        list.listContent = parts.content;
        list.itemTemplate = parts.template;
        list.emptyText = parts.emptyText;
        list.detailTitle = parts.detailTitle;
        list.detailMeta = parts.detailMeta;
        list.detailDesc = parts.detailDesc;
        list.openButton = openButton;
        list.refreshButton = parts.refreshButton;
        list.deleteButton = parts.deleteButton;
        list.closeButton = parts.closeButton;
        list.statusText = parts.statusText;
        list.countText = parts.countText;
        list.statusOpenButton = parts.statusOpenButton;
        list.statusProgressButton = parts.statusProgressButton;
        list.statusResolvedButton = parts.statusResolvedButton;

        list.searchInput = parts.searchInput;
        list.searchButton = parts.searchButton;
        list.deviceDropdown = parts.deviceDropdown;
        list.priorityDropdown = parts.priorityDropdown;
        list.statusDropdown = parts.statusDropdown;
        list.resetFilterButton = parts.resetFilterButton;

        list.reportModeButton = parts.reportModeButton;
        list.modeBanner = parts.modeBanner;

        // 三维联动接线（任务五）：
        //   列表 → 相机/标记：把主相机和射线脚本交给控制器；
        //   设备 → 列表：把控制器反过来交给射线脚本（点设备时回调 ShowDeviceRecords）。
        // 两边都接上，才能做到"列表点记录能定位、点设备能列出记录"。
        Camera main = Camera.main;
        if (main != null)
        {
            list.viewCamera = main;

            InspectionRaycaster ray = main.GetComponent<InspectionRaycaster>();
            if (ray != null)
            {
                list.raycaster = ray;
                ray.recordList = list;
            }
        }

        InspectionApiClient api = Object.FindObjectOfType<InspectionApiClient>();
        if (api != null) list.apiClient = api;

        return list;
    }

    // ---------------- 基础构件 ----------------

    /// <summary>建面板时攒下的引用，交给 Wire 接线用。
    /// 注意：这是普通类，不能继承 MonoBehaviour —— 嵌套的 MonoBehaviour Unity 认不出来。</summary>
    class PanelParts
    {
        public Button closeButton;
        public Button refreshButton;
        public Button deleteButton;
        public TMP_Text emptyText;
        public TMP_Text detailTitle;
        public TMP_Text detailMeta;
        public TMP_Text detailDesc;
        public TMP_Text statusText;
        public TMP_Text countText;
        public Button statusOpenButton;
        public Button statusProgressButton;
        public Button statusResolvedButton;
        public Transform content;
        public GameObject template;

        public TMP_InputField searchInput;
        public Button searchButton;
        public TMP_Dropdown deviceDropdown;
        public TMP_Dropdown priorityDropdown;
        public TMP_Dropdown statusDropdown;
        public Button resetFilterButton;

        public Button reportModeButton;
        public TMP_Text modeBanner;
    }

    /// <summary>筛选区攒下的引用。和 PanelParts 一样是普通类，不能继承 MonoBehaviour。</summary>
    class FilterParts
    {
        public GameObject root;
        public TMP_InputField searchInput;
        public Button searchButton;
        public TMP_Dropdown deviceDropdown;
        public TMP_Dropdown priorityDropdown;
        public TMP_Dropdown statusDropdown;
        public Button resetFilterButton;
    }

    static Color ColorNormal()
    {
        return new Color(0.96f, 0.96f, 0.96f, 1f);
    }

    static RectTransform AddRect(GameObject go, Transform parent, Vector2 size, Vector2 pos)
    {
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
    }

    static Image SetImage(GameObject go, Sprite sprite, Color color)
    {
        Image img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();

        img.sprite = sprite;
        img.color = color;
        img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        return img;
    }

    static void StyleText(TMP_Text t, string content, int size, FontStyles style,
        TextAlignmentOptions align, Color color)
    {
        t.font = font;
        t.text = content;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = color;
        t.enableWordWrapping = true;
        t.raycastTarget = false;
    }

    static TMP_Text Text(string name, Transform parent, string content, int size, FontStyles style,
        Vector2 sizeDelta, Vector2 pos, TextAlignmentOptions align, Color color)
    {
        GameObject go = new GameObject(name);
        AddRect(go, parent, sizeDelta, pos);
        TMP_Text t = go.AddComponent<TextMeshProUGUI>();
        StyleText(t, content, size, style, align, color);
        return t;
    }

    static Button Button(string name, Transform parent, string label, int fontSize,
        Vector2 sizeDelta, Vector2 pos, Color bgColor)
    {
        GameObject go = new GameObject(name);
        AddRect(go, parent, sizeDelta, pos);
        Image img = SetImage(go, spriteItem, bgColor);

        Button button = go.AddComponent<Button>();
        button.targetGraphic = img;

        if (!string.IsNullOrEmpty(label))
        {
            GameObject textGo = new GameObject("Text");
            AddRect(textGo, go.transform, sizeDelta, Vector2.zero);
            TMP_Text t = textGo.AddComponent<TextMeshProUGUI>();
            StyleText(t, label, fontSize, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
        }

        return button;
    }

    /// <summary>四边拉伸。参数用「距父物体四边的像素偏移」，跟 Inspector 里的 Left/Top/Right/Bottom 一致。</summary>
    static RectTransform StretchRect(GameObject go, Transform parent, float left, float top, float right, float bottom)
    {
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
        return rt;
    }

    /// <summary>关键词输入框。</summary>
    static TMP_InputField InputField(string name, Transform parent, string placeholder,
        Vector2 sizeDelta, Vector2 pos)
    {
        GameObject go = new GameObject(name);
        AddRect(go, parent, sizeDelta, pos);
        Image bg = SetImage(go, spriteField, new Color(0.99f, 0.99f, 1f, 1f));

        // 文本区：带 RectMask2D，输入超长时才不会顶出框外
        GameObject area = new GameObject("Text Area");
        RectTransform areaRT = StretchRect(area, go.transform, 14f, 6f, 14f, 6f);
        area.AddComponent<RectMask2D>();

        GameObject textGo = new GameObject("Text");
        StretchRect(textGo, area.transform, 0f, 0f, 0f, 0f);
        TMP_Text text = textGo.AddComponent<TextMeshProUGUI>();
        StyleText(text, "", 21, FontStyles.Normal, TextAlignmentOptions.MidlineLeft,
            new Color(0.13f, 0.13f, 0.15f));

        GameObject phGo = new GameObject("Placeholder");
        StretchRect(phGo, area.transform, 0f, 0f, 0f, 0f);
        TMP_Text ph = phGo.AddComponent<TextMeshProUGUI>();
        StyleText(ph, placeholder, 21, FontStyles.Italic, TextAlignmentOptions.MidlineLeft,
            new Color(0.55f, 0.55f, 0.58f));

        // Placeholder 要排在 Text 前面渲染，否则会盖在已输入的文字上
        phGo.transform.SetAsFirstSibling();

        TMP_InputField input = go.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = areaRT;
        input.textComponent = text;
        input.placeholder = ph;
        input.fontAsset = font;
        input.pointSize = 21;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 60;

        return input;
    }

    /// <summary>
    /// TMP 下拉框。缺 Viewport / Content / Item / Toggle 任何一个，运行时点开都会报错，
    /// 所以这里把 Unity 默认下拉的完整层级照搬一遍。
    /// 层级：Template(ScrollRect) → Viewport(RectMask2D) → Content → Item(Toggle + Background + Checkmark + Label)
    /// </summary>
    static TMP_Dropdown Dropdown(string name, Transform parent, List<string> options,
        Vector2 sizeDelta, Vector2 pos)
    {
        GameObject go = new GameObject(name);
        AddRect(go, parent, sizeDelta, pos);
        Image bg = SetImage(go, spriteField, new Color(0.99f, 0.99f, 1f, 1f));

        // 当前选中项的文字
        GameObject captionGo = new GameObject("Label");
        StretchRect(captionGo, go.transform, 14f, 4f, 34f, 4f);
        TMP_Text caption = captionGo.AddComponent<TextMeshProUGUI>();
        StyleText(caption, "", 20, FontStyles.Normal, TextAlignmentOptions.MidlineLeft,
            new Color(0.20f, 0.20f, 0.24f));

        // 右侧箭头
        GameObject arrowGo = new GameObject("Arrow");
        AddRect(arrowGo, go.transform, new Vector2(16f, 10f),
            new Vector2(sizeDelta.x * 0.5f - 18f, 0f));
        Image arrow = arrowGo.AddComponent<Image>();
        arrow.sprite = spriteArrow;
        arrow.color = new Color(0.45f, 0.45f, 0.50f);
        arrow.raycastTarget = false;

        // ---- 展开列表 ----
        GameObject template = new GameObject("Template");
        RectTransform trt = template.AddComponent<RectTransform>();
        trt.SetParent(go.transform, false);
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, 2f);
        trt.sizeDelta = new Vector2(0f, 220f);
        SetImage(template, spriteField, Color.white);
        ScrollRect scroll = template.AddComponent<ScrollRect>();

        GameObject viewport = new GameObject("Viewport");
        StretchRect(viewport, template.transform, 0f, 0f, 0f, 0f);
        viewport.AddComponent<Image>();
        viewport.AddComponent<RectMask2D>();

        GameObject content = new GameObject("Content");
        RectTransform crt = content.AddComponent<RectTransform>();
        crt.SetParent(viewport.transform, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0.5f, 1f);
        crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = new Vector2(0f, 40f);

        // ---- 单条选项 ----
        GameObject item = new GameObject("Item");
        RectTransform irt = item.AddComponent<RectTransform>();
        irt.SetParent(content.transform, false);
        irt.anchorMin = new Vector2(0f, 0.5f);
        irt.anchorMax = new Vector2(1f, 0.5f);
        irt.pivot = new Vector2(0.5f, 0.5f);
        irt.anchoredPosition = Vector2.zero;
        irt.sizeDelta = new Vector2(0f, 40f);

        GameObject itemBg = new GameObject("Item Background");
        StretchRect(itemBg, item.transform, 0f, 0f, 0f, 0f);
        Image ibg = SetImage(itemBg, spriteItem, new Color(1f, 1f, 1f, 0.02f));

        GameObject itemLabelGo = new GameObject("Item Label");
        StretchRect(itemLabelGo, item.transform, 14f, 2f, 34f, 2f);
        TMP_Text itemLabel = itemLabelGo.AddComponent<TextMeshProUGUI>();
        StyleText(itemLabel, "", 20, FontStyles.Normal, TextAlignmentOptions.MidlineLeft,
            new Color(0.20f, 0.20f, 0.24f));

        // 选中标记：TMP_Dropdown 用 itemImage 当 checkmark，不能拿背景图顶替
        GameObject checkGo = new GameObject("Item Checkmark");
        RectTransform checkRt = checkGo.AddComponent<RectTransform>();
        checkRt.SetParent(item.transform, false);
        checkRt.anchorMin = new Vector2(1f, 0.5f);
        checkRt.anchorMax = new Vector2(1f, 0.5f);
        checkRt.pivot = new Vector2(1f, 0.5f);
        checkRt.anchoredPosition = new Vector2(-14f, 0f);
        checkRt.sizeDelta = new Vector2(16f, 16f);
        Image checkImg = checkGo.AddComponent<Image>();
        checkImg.sprite = spriteDot;
        checkImg.color = new Color(0.20f, 0.55f, 0.86f);
        checkImg.raycastTarget = false;

        Toggle toggle = item.AddComponent<Toggle>();
        toggle.targetGraphic = ibg;
        toggle.graphic = checkImg;

        scroll.content = crt;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        TMP_Dropdown dropdown = go.AddComponent<TMP_Dropdown>();
        dropdown.targetGraphic = bg;
        dropdown.template = trt;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.itemImage = checkImg;
        dropdown.captionImage = null;

        // 必须默认关闭：展开时 TMP 会拿它当模板实例化
        template.SetActive(false);

        dropdown.ClearOptions();
        dropdown.AddOptions(options);
        dropdown.value = 0;
        dropdown.RefreshShownValue();

        return dropdown;
    }
}
