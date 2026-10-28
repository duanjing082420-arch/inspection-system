// IssueFormPanelBuilder.cs
//
// 在 Tuanjie / Unity 编辑器里一键生成「新增巡检问题」上报面板（IssueFormPanel）。
// 尺寸、位置、颜色、字体、占位符、下拉项、按钮事件都按截图效果自动设置，
// 并自动把 IssueFormController 组件的引用接好。
//
// 用法：菜单 Tools ▸ 巡检 ▸ 创建上报面板 IssueFormPanel
//      （重复执行会先删除旧面板，避免重影）
//
// 本文件必须放在 Assets/Editor 下（仅编辑器编译，不会打进真机包）。
//
// 布局说明：
//   参考分辨率 750x1334（竖屏手机），CanvasScaler match=0.5。
//   面板 660x860 居中，内边距 30，内容区宽 600。
//   所有子物体的 Pos X/Pos Y 都是相对面板中心的偏移（anchor / pivot 均为中心）。

using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

public static class IssueFormPanelBuilder
{
    // ---------- 面板尺寸 ----------
    const float PANEL_W = 660f;
    // 930：比原来的 860 多出 70，用来放新增的「设备名称」一行
    const float PANEL_H = 930f;
    const float CONTENT_W = 600f;   // 内容区宽度（面板 660 - 左右各 30 内边距）

    const float PANEL_RADIUS = 24f; // 卡片圆角
    const float BOX_RADIUS = 12f;   // 输入框 / 按钮圆角
    const float HEADER_RADIUS = 18f;

    const string FONT_PATH = "Assets/Fonts/6a40e936c7590 SDF.asset"; // 旧的微软雅黑 TMP 资产（362 号字，图集已满，仅作兜底）
    const string FONT_TTF = "Assets/Fonts/6a40e936c7590.ttf";         // 原始 ttf
    const string FONT_DYNAMIC_PATH = "Assets/Fonts/InspectionYaHei SDF.asset"; // 本脚本生成的动态字体
    const string SPRITE_DIR = "Assets/UI";

    // 面板里会用到的全部字符。原字体资产只有 43 个字形（Static 子集），
    // 缺「占/文/字/请/输/入」等字就会渲染成方块，所以这里一次性补齐烘进图集。
    const string NEEDED_CHARS =
        "新增巡检问题现场记录标题描述选填优先级低中高命中位置世界坐标" +
        "提交取消上报成功失败网络错误未知请在一场景里点击确认返回关闭重试" +
        "问题已发送后端服务连接不上检查地址是否填写正确" +
        "“”中文测试" +
        // 设备名称一行会直接显示场景物体名（Cabinet_A / Pump_Unit / wall 等），
        // 这些字母必须预先烘进图集，否则首次弹出表单时会临时补字、有一瞬间的方块
        "设备名称未识别" +
        "Cabinet_DoorGreenLightRedPumpUnitPipeValveHorizontalVerticalGroundWallABCD" +
        "0123456789.:-—()（）*·，。： XYZ";

    // ---------- 配色（取自设计稿） ----------
    static readonly Color32 C_PRIMARY = new Color32(0x17, 0x69, 0xAA, 0xFF); // 主蓝
    static readonly Color32 C_TITLE = new Color32(0x1F, 0x2D, 0x3D, 0xFF);   // 深色标题
    static readonly Color32 C_LABEL = new Color32(0x26, 0x36, 0x4A, 0xFF);   // 字段标签
    static readonly Color32 C_HINT = new Color32(0x8A, 0x96, 0xA5, 0xFF);    // 占位文字
    static readonly Color32 C_VALUE = new Color32(0x34, 0x50, 0x6C, 0xFF);   // 坐标文字
    static readonly Color32 C_BOX_BG = new Color32(0xF7, 0xFA, 0xFD, 0xFF);  // 输入框底
    static readonly Color32 C_BOX_LINE = new Color32(0xD8, 0xE3, 0xEE, 0xFF); // 输入框描边
    static readonly Color32 C_POS_BG = new Color32(0xEF, 0xF5, 0xFA, 0xFF);  // 坐标框底
    static readonly Color32 C_CANCEL_BG = new Color32(0xE7, 0xED, 0xF4, 0xFF); // 取消按钮底
    static readonly Color32 C_CANCEL_TX = new Color32(0x34, 0x46, 0x5A, 0xFF);

    [MenuItem("Tools/巡检/创建上报面板 IssueFormPanel", false, 1)]
    static void Build()
    {
        // Play 模式下对场景的任何改动，退出 Play 都会被 Unity 回滚。
        // 之前几次「改了没生效」就是这个原因，所以这里直接拦掉。
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[巡检] 请先退出 Play 模式，再生成面板。Play 模式下的改动不会被保存。");
            return;
        }

        // ---------- 0) 字体：确保是 Dynamic 且字形齐全（否则中文变方块） ----------
        TMP_FontAsset font = EnsureFont();

        // ---------- 1) Canvas / EventSystem ----------
        Canvas canvas = FindOverlayCanvas();
        if (canvas == null)
        {
            var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = cgo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750, 1334);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        // 按宽度匹配：面板永远占 88% 屏宽，字号随宽度等比放大。
        // 之前用 0.5 在编辑器的扁窗口里会被高度拉低到 0.6 倍，字只有 19px，怎么看都糊。
        scaler.matchWidthOrHeight = 0f;
        scaler.referencePixelsPerUnit = 100;

        if (Object.FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        // ---------- 2) 清掉所有旧面板，避免重复执行后重影 ----------
        //   注意：旧的那块面板不是本脚本生成的（子物体全是默认 200x50@0,0），
        //   所以这里按名字全局搜，不限定父节点。
        int removed = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t != null && t.name == "IssueFormPanel")
            {
                Object.DestroyImmediate(t.gameObject);
                removed++;
            }
        }
        if (removed > 0)
            Debug.Log("[巡检] 已清除 " + removed + " 个旧 IssueFormPanel。");

        // ---------- 3) 卡片本体（白底圆角 + 轻微阴影） ----------
        var panel = NewUI("IssueFormPanel", canvas.transform);
        SetRect(panel, 0, 0, PANEL_W, PANEL_H, new Vector2(0.5f, 0.5f));
        var panelImg = panel.AddComponent<Image>();
        panelImg.sprite = RoundedSprite(PANEL_RADIUS);
        panelImg.type = Image.Type.Sliced;
        panelImg.color = Color.white;
        panelImg.raycastTarget = true; // 挡住面板背后的点击，避免误放标记

        // ---------- 4) 顶部蓝色标题栏 ----------
        //   面板半高 430，标题栏 110，上边距 30 -> 中心 y = 430 - 30 - 55 = 345
        var header = NewUI("Header", panel.transform);
        SetRect(header, 0, 380, CONTENT_W, 110, new Vector2(0.5f, 0.5f));
        var headerImg = header.AddComponent<Image>();
        headerImg.sprite = RoundedSprite(HEADER_RADIUS);
        headerImg.type = Image.Type.Sliced;
        headerImg.color = C_PRIMARY;

        // 标题栏只放标题一行，垂直居中。早先多塞了一行 18 号副标题，
        // 在 750 参考宽度缩到 0.6 倍时只剩 11px，糊成一团，已去掉。
        var formTitle = AddText("FormTitle", header.transform, "新增巡检问题", 34, font,
            TextAlignmentOptions.MidlineLeft, Color.white, FontStyles.Bold);
        StretchRect(formTitle, 24, 0, -24, 0); // left, bottom, right, top

        // ---------- 5) 表单字段 ----------
        AddLabel("TitleLabel", panel, "问题标题 *", font, 283);
        var titleInput = AddInput("TitleInput", panel, "请输入问题标题", font, 20, 227, 56, false);

        AddLabel("DescriptionLabel", panel, "问题描述", font, 169);
        var descInput = AddInput("DescriptionInput", panel, "请描述现场发现的问题（选填）", font, 18, 97, 104, true);

        AddLabel("PriorityLabel", panel, "问题优先级", font, 27);
        var priorityDropdown = AddDropdown("PriorityDropdown", panel, font, -29, 56);

        // 设备名称：显示射线命中的巡检对象。点柜门/指示灯时由射线脚本归到所属机柜，
        // 所以这里看到的是 Cabinet_A，而不是 Cabinet_Door。
        AddLabel("ObjectLabel", panel, "巡检对象", font, -97);
        var objectText = AddInfoBox("ObjectText", panel, "—", font, 20, -139, 52);

        AddLabel("PositionLabel", panel, "命中位置（世界坐标）", font, -210);
        var positionText = AddInfoBox("PositionText", panel,
            "X: 0.00   Y: 0.00   Z: 0.00", font, 20, -252, 52);

        // ---------- 6) 底部按钮 ----------
        var submit = AddButton("SubmitButton", panel, "提交问题", font, 22,
            C_PRIMARY, Color.white, -152, -346, 296, 60);
        var cancel = AddButton("CancelButton", panel, "取消", font, 22,
            C_CANCEL_BG, C_CANCEL_TX, 152, -346, 296, 60);

        // ---------- 7) 提交结果提示 ----------
        var statusGo = AddText("StatusText", panel.transform, "", 20, font,
            TextAlignmentOptions.Center, C_PRIMARY, FontStyles.Normal);
        SetRect(statusGo, 0, -409, CONTENT_W, 34, new Vector2(0.5f, 0.5f));
        var status = statusGo.GetComponent<TMP_Text>();

        // ---------- 8) 挂 IssueFormController 并自动接线 ----------
        // 清掉可能残留的旧控制器组件，避免重复挂载两份
        var staleFC = panel.GetComponent<IssueFormController>();
        if (staleFC != null) Object.DestroyImmediate(staleFC);

        var rp = panel.AddComponent<IssueFormController>();
        rp.panelRoot = panel;
        rp.titleInput = titleInput;
        rp.descInput = descInput;
        rp.priorityDropdown = priorityDropdown;
        rp.objectText = objectText;
        rp.positionText = positionText;
        rp.statusText = status;
        // 显式写死本机地址：场景里序列化的旧值（192.168.1.100）不会因为改代码默认值而更新
        rp.serverUrl = "http://127.0.0.1:8080";

        submit.onClick.AddListener(rp.OnSubmit);
        cancel.onClick.AddListener(rp.Cancel);

        // ---------- 8.5) 顺手把 InspectionRaycaster 挂到主相机并绑定面板 ----------
        // （当前场景里还没挂任何脚本，这一步省掉手动拖拽）
        var cam = Camera.main;
        if (cam != null)
        {
            var raycaster = cam.GetComponent<InspectionRaycaster>();
            if (raycaster == null)
                raycaster = Undo.AddComponent<InspectionRaycaster>(cam.gameObject);

            raycaster.targetCamera = cam;
            raycaster.formController = rp;
            // 标记球直径：0.14 米左右在 2.4 米高的机柜旁刚好看得清又不挡视线
            raycaster.markerSize = 0.14f;

            // 视角控制器（WASD / QE / 右键拖动）。之前只挂了射线脚本、没挂它，
            // 结果就是「按什么键都没反应」。这里一并挂上并绑定，省掉手动拖拽。
            var camCtrl = cam.GetComponent<InspectionCamera>();
            if (camCtrl == null)
                camCtrl = Undo.AddComponent<InspectionCamera>(cam.gameObject);
            camCtrl.raycaster = raycaster;

            Debug.Log("[巡检] 已把 InspectionRaycaster + InspectionCamera 挂在 Main Camera 上，并绑定 Issue Form Controller。");
        }
        else
        {
            Debug.LogWarning("[巡检] 场景里没找到 Main Camera（或它的 Tag 不是 MainCamera）。" +
                             "请手动把 InspectionRaycaster 挂到相机上，再把 IssueFormPanel 拖到 Form Controller 字段。");
        }

        // ---------- 9) 收尾 + 自检 ----------
        Undo.RegisterCreatedObjectUndo(panel, "创建巡检上报面板");
        Selection.activeGameObject = panel;
        EditorSceneManager.MarkSceneDirty(panel.scene);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[巡检] 面板已生成：" + PANEL_W + " x " + PANEL_H + "（居中）");
        foreach (var rt in panel.GetComponentsInChildren<RectTransform>(true))
        {
            if (rt == panel.transform) continue;
            sb.AppendLine("  " + rt.name + "  pos=(" + rt.anchoredPosition.x.ToString("0") + ", " +
                          rt.anchoredPosition.y.ToString("0") + ")  size=" +
                          rt.sizeDelta.x.ToString("0") + " x " + rt.sizeDelta.y.ToString("0"));
        }
        sb.AppendLine("下一步：Ctrl+S 保存场景 → 点 Play → 点一下墙面试试。");
        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/巡检/删除上报面板", false, 2)]
    static void Remove()
    {
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t.name == "IssueFormPanel")
            {
                Object.DestroyImmediate(t.gameObject);
                Debug.Log("[巡检] 已删除 IssueFormPanel。");
                return;
            }
        }
        Debug.Log("[巡检] 场景里没有 IssueFormPanel。");
    }

    // =====================================================================
    //  字体
    // =====================================================================

    /// <summary>
    /// 取一个能显示中文的 TMP 字体。
    /// 原来那个 6a40e936c7590 SDF.asset 是 362 号字 + 2048² 图集 + 只有 43 个字形（Static 子集），
    /// 图集几乎塞满，既补不进新字，字号也远大于 UI 需要 —— 这就是截图里一大堆方块的来源。
    /// 这里直接拿同目录的 ttf 现场生成一个 Dynamic 字体资产，缺什么字运行时现生成。
    /// </summary>
    static TMP_FontAsset EnsureFont()
    {
        // SDF 采样 90 号 + padding 9：padding 决定材质里的 _GradientScale（= pad*2+1），
        // padding 5 时只有 11，小字号下 SDF 距离场太薄，笔画边缘会发虚、粘连。
        // 这是「文字看起来糊」最直接的补偿手段，与 Game 视图的预览缩放无关。
        const int POINT_SIZE = 90;
        const int PADDING = 9;

        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FONT_DYNAMIC_PATH);
        if (existing != null && (int)existing.faceInfo.pointSize != POINT_SIZE)
        {
            Debug.Log("[巡检] 旧字体采样 " + (int)existing.faceInfo.pointSize +
                      " 号，重建为 " + POINT_SIZE + " 号。");
            AssetDatabase.DeleteAsset(FONT_DYNAMIC_PATH);
            existing = null;
        }
        if (existing != null)
        {
            BakeCommonChars(existing);
            return existing;
        }

        var srcFont = AssetDatabase.LoadAssetAtPath<Font>(FONT_TTF);
        if (srcFont == null)
        {
            Debug.LogWarning("[巡检] 没找到 " + FONT_TTF + "，回退到旧字体资产。中文可能仍是方块。");
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FONT_PATH) ?? TMP_Settings.defaultFontAsset;
        }

        // 90 号采样 + 2048² 图集 + 多图集：面板 60 来个字只占一小块，后续扩字也不会撑爆
        var fa = TMP_FontAsset.CreateFontAsset(
            srcFont,
            POINT_SIZE,
            PADDING,
            GlyphRenderMode.SDFAA,
            2048, 2048,
            AtlasPopulationMode.Dynamic,
            true);

        if (fa == null)
        {
            Debug.LogError("[巡检] 生成动态字体失败，回退到旧字体资产。");
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FONT_PATH) ?? TMP_Settings.defaultFontAsset;
        }

        fa.name = "InspectionYaHei SDF";
        AssetDatabase.CreateAsset(fa, FONT_DYNAMIC_PATH);

        // 图集贴图和材质必须作为子资产一起存盘，否则打包后会丢，真机上文字全空
        if (fa.atlasTextures != null)
        {
            for (int i = 0; i < fa.atlasTextures.Length; i++)
            {
                var t = fa.atlasTextures[i];
                if (t == null) continue;
                t.name = fa.name + " Atlas " + i;
                AssetDatabase.AddObjectToAsset(t, fa);
            }
        }
        if (fa.material != null)
        {
            fa.material.name = fa.name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(FONT_DYNAMIC_PATH, ImportAssetOptions.ForceSynchronousImport);

        // 重新加载一遍，确认图集/材质确实落盘（否则真机上会显示为空）
        var reloaded = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FONT_DYNAMIC_PATH);
        if (reloaded == null || reloaded.atlasTexture == null || reloaded.material == null)
        {
            Debug.LogError("[巡检] 动态字体资产落盘不完整（图集或材质丢失），请手动检查 " + FONT_DYNAMIC_PATH);
            return fa;
        }

        BakeCommonChars(reloaded);
        Debug.Log("[巡检] 已生成动态字体：" + FONT_DYNAMIC_PATH +
                  "，图集 " + reloaded.atlasTexture.width + "x" + reloaded.atlasTexture.height);
        return reloaded;
    }

    /// <summary>把面板会用到的字符预先生成进图集（Dynamic 模式下不预烘也能显示，只是首次渲染会慢一点）。</summary>
    static void BakeCommonChars(TMP_FontAsset font)
    {
        if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic)
            font.atlasPopulationMode = AtlasPopulationMode.Dynamic;

        string missing;
        if (font.TryAddCharacters(NEEDED_CHARS, out missing))
            Debug.Log("[巡检] 字形预生成完成，当前字形数 = " + font.glyphTable.Count);
        else
            Debug.LogWarning("[巡检] 部分字形未预生成（不影响使用，运行时会补）：" + missing);

        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/巡检/修复字体缺字（中文变方块）", false, 3)]
    static void FixFont()
    {
        EnsureFont();
        Debug.Log("[巡检] 字体修复完成。若文字仍显示方块，请重新执行「创建上报面板」。");
    }

    /// <summary>
    /// 打开「允许 HTTP 明文下载」开关。
    /// Unity 2020.2+ 默认禁止 http://，本地后端是明文 http，提交时会抛
    /// InvalidOperationException: Insecure connection not allowed。
    /// 这里用反射改 PlayerSettings，避免 API 命名空间差异导致整个文件编译失败
    /// （一编译失败，所有菜单都会消失，这个亏已经吃过一次）。
    /// </summary>
    [MenuItem("Tools/巡检/允许 HTTP 明文请求（本地后端）", false, 4)]
    static void AllowInsecureHttp()
    {
        var prop = typeof(PlayerSettings).GetProperty(
            "insecureHttpOption",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (prop == null)
        {
            Debug.LogError("[巡检] 当前引擎版本没有 PlayerSettings.insecureHttpOption。" +
                "请手动改：Project Settings ▸ Player ▸ Other Settings ▸ Allow downloads over HTTP ▸ Always allowed");
            return;
        }

        object alwaysAllowed = System.Enum.Parse(prop.PropertyType, "AlwaysAllowed");
        prop.SetValue(null, alwaysAllowed, null);
        AssetDatabase.SaveAssets();

        Debug.Log("[巡检] 已允许 HTTP 明文请求（insecureHttpOption = AlwaysAllowed）。重新进 Play 生效。");
    }

    /// <summary>
    /// 一键自检：把「表单 6 个控件 + 场景接线」逐项查一遍并打印报告。
    /// 手动挨个点很费劲，而且有些问题（引用没接上、控制器挂了多份）肉眼看不出来。
    /// </summary>
    [MenuItem("Tools/巡检/自检：表单与场景接线", false, 5)]
    static void SelfCheck()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("===== 巡检系统自检 =====");
        int pass = 0, fail = 0;

        System.Action<bool, string, string> line = (ok, label, detail) =>
        {
            sb.AppendLine((ok ? "  [正常] " : "  [异常] ") + label + " — " + detail);
            if (ok) pass++; else fail++;
        };

        // ---------- 1) 控制器本体 ----------
        var ctrls = Resources.FindObjectsOfTypeAll<IssueFormController>();
        line(ctrls.Length == 1, "IssueFormController 份数",
            ctrls.Length + " 份" + (ctrls.Length > 1 ? "（多于 1 份，需清理）" : ""));
        if (ctrls.Length == 0)
        {
            sb.AppendLine("===== 未找到 IssueFormController，请先执行「创建上报面板 IssueFormPanel」=====");
            Debug.LogWarning(sb.ToString());
            return;
        }
        var c = ctrls[0];

        // ---------- 2) 面板编辑期必须激活（否则 Awake 不跑，按钮全失效） ----------
        var pr = c.panelRoot != null ? c.panelRoot : c.gameObject;
        line(pr.activeSelf, "面板编辑期激活",
            pr.name + ".activeSelf=" + pr.activeSelf + "（必须为 True，否则运行时脚本不初始化）");

        // ---------- 3) 表单 6 项控件引用 ----------
        line(c.titleInput != null, "标题输入框", c.titleInput != null ? c.titleInput.name : "未接线");
        if (c.titleInput != null)
            line(c.titleInput.textComponent != null && c.titleInput.targetGraphic != null,
                "标题输入框可交互", "textComponent/targetGraphic 已接（缺一就无法点进去打字）");

        line(c.descInput != null, "问题描述输入框", c.descInput != null ? c.descInput.name : "未接线");
        if (c.descInput != null)
            line(c.descInput.textComponent != null && c.descInput.targetGraphic != null,
                "描述输入框可交互", "textComponent/targetGraphic 已接");

        line(c.priorityDropdown != null, "优先级下拉框", c.priorityDropdown != null ? c.priorityDropdown.name : "未接线");
        if (c.priorityDropdown != null)
            line(c.priorityDropdown.options != null && c.priorityDropdown.options.Count == 3,
                "下拉项数量", (c.priorityDropdown.options != null ? c.priorityDropdown.options.Count : 0) + " 项（应为 3：低/中/高）");

        line(c.objectText != null, "巡检对象显示", c.objectText != null ? c.objectText.name : "未接线");

        // ---------- 3.5) 设备标识 InspectionTarget 覆盖情况 ----------
        // 没挂组件的零件会退回显示自身名字，这是「点 Motor_Body 却显示 Motor_Body」的唯一原因，
        // 所以这里直接把 8 台设备的覆盖情况列出来，不用一台台点着试。
        var targets = Object.FindObjectsOfType<InspectionTarget>(true);
        string[] need = { "Pump_Unit_A", "Pump_Unit_B", "Pump_Unit_C", "Valve_A",
                          "Cabinet_A", "Cabinet_B", "Cabinet_C", "Cabinet_D" };
        int missing = 0;
        foreach (string n in need)
        {
            bool has = false;
            foreach (var t in targets) { if (t != null && t.gameObject.name == n) { has = true; break; } }
            if (!has) missing++;
        }
        line(missing == 0, "设备巡检标识 InspectionTarget",
            missing == 0
                ? "8 台设备全部已挂（" + string.Join("/", need) + "）"
                : "还有 " + missing + " 台没挂，请执行 Tools ▸ 巡检场景 ▸ 第12步 设备巡检标识 InspectionTarget");
        line(c.positionText != null, "坐标显示", c.positionText != null ? c.positionText.name : "未接线");
        line(c.statusText != null, "状态提示", c.statusText != null ? c.statusText.name : "未接线");

        // ---------- 3.5) 后端通信客户端：必须恰好一份 ----------
        // 0 份 → 提交时才会运行时现建（能用但没存进场景）；
        // >1 份 → 一次提交会发多个请求，后端出现重复记录。
        var apiClients = Object.FindObjectsOfType<InspectionApiClient>(true);
        line(apiClients != null && apiClients.Length == 1,
            "后端通信 InspectionApiClient",
            apiClients == null || apiClients.Length == 0
                ? "场景里没有，运行时会临时创建（建议执行 第13步 后端服务 InspectionServices）"
                : apiClients.Length + " 份（挂在 " + apiClients[0].gameObject.name + "）" +
                  (apiClients.Length > 1 ? " — 重复了，请执行第13步清理" : ""));

        if (apiClients != null && apiClients.Length >= 1 && apiClients[0] != null)
        {
            string url = apiClients[0].ApiUrl;
            bool urlOk = !string.IsNullOrWhiteSpace(url)
                         && (url.StartsWith("http://") || url.StartsWith("https://"))
                         && !url.Contains("192.168.1.100");
            line(urlOk, "后端接口地址", string.IsNullOrWhiteSpace(url) ? "（空）" : url);
        }

        // ---------- 4) 两个按钮存在且可点击 ----------
        var submitT = FindDeep(pr.transform, "SubmitButton");
        var cancelT = FindDeep(pr.transform, "CancelButton");
        line(submitT != null && submitT.GetComponent<Button>() != null,
            "提交按钮", submitT != null ? submitT.name + "（含 Button 组件）" : "未找到 SubmitButton");
        line(cancelT != null && cancelT.GetComponent<Button>() != null,
            "取消按钮", cancelT != null ? cancelT.name + "（含 Button 组件）" : "未找到 CancelButton");

        // ---------- 4.5) 按钮 onClick 绑定了什么 ----------
        CheckButtonBinding(line, submitT, "提交按钮", "OnSubmit");
        CheckButtonBinding(line, cancelT, "取消按钮", "Cancel");

        // ---------- 4.6) 可交互性 + 父级链（父级藏了 / CanvasGroup 拦截，孩子照样点不动） ----------
        CheckInteractable(line, "标题输入框", c.titleInput);
        CheckInteractable(line, "描述输入框", c.descInput);
        CheckInteractable(line, "优先级下拉框", c.priorityDropdown);
        CheckInteractable(line, "提交按钮", submitT != null ? submitT.GetComponent<Button>() : null);
        CheckInteractable(line, "取消按钮", cancelT != null ? cancelT.GetComponent<Button>() : null);

        // ---------- 5) UI 能接收点击的前提 ----------
        var es = Object.FindObjectOfType<EventSystem>();
        line(es != null, "EventSystem", es != null ? es.name : "场景中不存在（UI 完全点不动）");

        var canvas = pr.GetComponentInParent<Canvas>();
        line(canvas != null && canvas.GetComponent<GraphicRaycaster>() != null,
            "Canvas + GraphicRaycaster",
            canvas != null
                ? (canvas.GetComponent<GraphicRaycaster>() != null ? canvas.name + "（已挂载）" : canvas.name + "（缺 GraphicRaycaster）")
                : "面板不在 Canvas 层级下");

        // ---------- 5.5) Canvas 渲染模式 + 输入模块 ----------
        if (canvas != null)
        {
            string mode;
            bool modeOk = true;
            switch (canvas.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    mode = "覆盖模式，UI 永远在最上层，不依赖相机";
                    break;
                case RenderMode.ScreenSpaceCamera:
                    modeOk = canvas.worldCamera != null;
                    mode = modeOk ? "相机模式，Render Camera = " + canvas.worldCamera.name
                                  : "相机模式，但未指定 Render Camera → UI 不会显示";
                    break;
                default:
                    modeOk = canvas.worldCamera != null;
                    mode = modeOk ? "世界空间，Event Camera = " + canvas.worldCamera.name
                                  : "世界空间，但未指定 Event Camera → 点击可能失效";
                    break;
            }
            line(modeOk, "Canvas 渲染模式", canvas.renderMode + "：" + mode);
            line(canvas.enabled, "Canvas 组件启用", "enabled=" + canvas.enabled);
        }

        if (es != null)
        {
            var modules = es.GetComponents<BaseInputModule>();
            line(modules.Length >= 1, "EventSystem 输入模块",
                modules.Length == 0
                    ? "0 个（没有任何输入模块，鼠标/键盘完全无响应）"
                    : modules.Length + " 个：" + string.Join(", ",
                        System.Array.ConvertAll(modules, m => m.GetType().Name + "(enabled=" + m.enabled + ")")));
            if (modules.Length > 1)
                line(false, "输入模块份数", "挂了 " + modules.Length + " 个，会互相抢输入，保留 1 个即可");
            line(es.enabled, "EventSystem 启用", "enabled=" + es.enabled);
        }

        // ---------- 5.6) 射线与 UI 会不会冲突 ----------
        CheckRaycastUiConflict(line);

        // ---------- 6) 主相机接线 ----------
        var camGo = GameObject.Find("Main Camera");
        if (camGo == null)
        {
            line(false, "Main Camera", "场景中未找到名为 Main Camera 的物体");
        }
        else
        {
            var rays = camGo.GetComponents<InspectionRaycaster>();
            line(rays.Length == 1, "InspectionRaycaster 份数",
                rays.Length + " 份" + (rays.Length > 1 ? "（多于 1 份，会重复放标记）" : ""));
            if (rays.Length >= 1)
            {
                line(rays[0].formController != null, "raycaster.formController",
                    rays[0].formController != null ? "已绑定（表单会自动弹出）" : "None（致命：表单不会弹出）");
                line(Mathf.Abs(rays[0].markerSize - 0.14f) < 0.001f, "markerSize",
                    rays[0].markerSize + "（建议 0.14）");
            }

            var cams = camGo.GetComponents<InspectionCamera>();
            line(cams.Length == 1, "InspectionCamera 份数",
                cams.Length + " 份" + (cams.Length > 1 ? "（多于 1 份，WASD 位移会叠加）" : ""));

            line(camGo.transform.position.y > 0.5f, "相机高度",
                "y=" + camGo.transform.position.y.ToString("F2") + "（<0.5 等于贴地，只看得见地板）");
        }

        // ---------- 7) 后端地址（空值 / 协议 / 端口 / 占位 / 运行环境，逐项查） ----------
        CheckServerUrl(line, c.serverUrl);

        // ---------- 7.5) 表单地址与通信地址是否一致 ----------
        // 旧版两边各存一份地址、实际以 apiClient 为准，在 Inspector 改 serverUrl 不生效。
        // 整合版会在运行时同步，但这里先报出来，省得手动排查。
        if (apiClients != null && apiClients.Length >= 1 && apiClients[0] != null)
        {
            string api = apiClients[0].ApiUrl ?? "";
            string baseUrl = string.IsNullOrWhiteSpace(c.serverUrl) ? "" : c.serverUrl.Trim().TrimEnd('/');
            bool consistent = baseUrl.Length > 0 &&
                              api.StartsWith(baseUrl, System.StringComparison.OrdinalIgnoreCase);
            line(consistent, "表单地址 ↔ 通信地址",
                "serverUrl=" + c.serverUrl + " / apiClient.ApiUrl=" + api
                + (consistent ? "（一致）" : "（不一致，运行时会按 serverUrl 自动同步）"));
        }

        // ---------- 7.6) 本地待发队列（提交失败留下的记录） ----------
        // 有残留说明上次有提交没成功 —— 这是「失败后丢记录」的对账入口。
        int pendingCount = 0;
        string outboxPath = InspectionApiClient.OutboxPath;
        try
        {
            if (File.Exists(outboxPath))
            {
                var ob = JsonUtility.FromJson<InspectionOutboxFile>(
                    File.ReadAllText(outboxPath, System.Text.Encoding.UTF8));
                pendingCount = (ob != null && ob.items != null) ? ob.items.Count : 0;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[巡检] 读取本地队列失败：" + e.Message);
        }

        line(pendingCount == 0, "本地待发队列",
            pendingCount == 0
                ? "0 条（没有未提交的记录）"
                : pendingCount + " 条未提交，下次进入 Play 会自动重发（Tools ▸ 巡检 ▸ 待重发记录：查看 看明细）");

        // ---------- 7.7) 优先级映射：中文显示值 -> 英文上报值 ----------
        // 下拉框给用户看的是「低/中/高」，JSON 里必须是 low/medium/high。
        // 中文直发会被后端 400 拒掉（实测 priority:"紧急" -> 400），
        // 所以这里逐个把下拉项翻译一遍，确认每一档都能对上英文枚举。
        if (c.priorityDropdown != null && c.priorityDropdown.options != null)
        {
            var opts = c.priorityDropdown.options;
            var map = new System.Text.StringBuilder();
            bool mapOk = opts.Count == 3;

            for (int i = 0; i < opts.Count; i++)
            {
                string label = opts[i].text;
                string value = InspectionApiClient.NormalizePriority(label);
                if (value == null) mapOk = false;

                map.Append("「").Append(label).Append("」->").Append(value ?? "无法识别");
                if (i < opts.Count - 1) map.Append("，");
            }

            line(mapOk, "优先级映射（显示值→上报值）", map.ToString());
        }
        else
        {
            line(false, "优先级映射（显示值→上报值）", "下拉框未绑定，无法检查");
        }

        sb.AppendLine("===== 通过 " + pass + " 项，异常 " + fail + " 项 =====");
        if (fail > 0) Debug.LogWarning(sb.ToString()); else Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 按钮 onClick 到底绑了什么。
    /// 坑点：builder 在编辑期用 AddListener 加的是「非持久化监听器」，不会序列化进场景，
    /// 所以编辑期 persistentCount = 0 是正常现象，不是漏绑 —— 真正生效的是
    /// IssueFormController.Awake() 里的运行时绑定。这里用反射确认目标方法确实存在
    /// （public + 无参），否则会出现「绑了个不存在的方法」这种静默失效。
    /// </summary>
    static void CheckButtonBinding(System.Action<bool, string, string> line,
        Transform t, string label, string expectMethod)
    {
        if (t == null) return;
        var btn = t.GetComponent<Button>();
        if (btn == null) return;

        int persistent = btn.onClick.GetPersistentEventCount();
        bool ok;
        string detail;

        if (persistent > 0)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < persistent; i++)
                names.Add(btn.onClick.GetPersistentMethodName(i));
            ok = names.Contains(expectMethod);
            detail = "持久化 " + persistent + " 个：" + string.Join(", ", names)
                     + (ok ? "（含 " + expectMethod + "）" : "（缺少 " + expectMethod + "）");
        }
        else
        {
            var mi = typeof(IssueFormController).GetMethod(expectMethod,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, System.Type.EmptyTypes, null);
            ok = mi != null;
            detail = "编辑期无持久化监听器（正常：AddListener 不序列化）；"
                     + "运行时由 IssueFormController.Awake() 绑 " + expectMethod
                     + (ok ? "，该方法存在且为 public 无参 ✓" : "，找不到该 public 无参方法 ✗");
        }

        line(ok, label + "·onClick", detail);
    }

    /// <summary>
    /// 可交互性必须查三层：组件自身 interactable → 组件 enabled → 整条父级链。
    /// 父级 activeSelf=false，或任一层 CanvasGroup 设了 interactable=false / blocksRaycasts=false，
    /// 孩子照样点不动 —— 只查组件自身会漏掉这类问题。
    /// </summary>
    static void CheckInteractable(System.Action<bool, string, string> line, string label, Selectable comp)
    {
        if (comp == null) return;

        var issues = new System.Collections.Generic.List<string>();

        if (!comp.interactable) issues.Add("自身 interactable=false");
        if (!comp.enabled) issues.Add("自身 enabled=false");

        Transform t = comp.transform.parent;
        while (t != null)
        {
            if (!t.gameObject.activeSelf) issues.Add("父级 " + t.name + " 未激活");
            t = t.parent;
        }

        foreach (var g in comp.GetComponentsInParent<CanvasGroup>(true))
        {
            if (!g.interactable) issues.Add("CanvasGroup " + g.name + " interactable=false");
            if (!g.blocksRaycasts) issues.Add("CanvasGroup " + g.name + " blocksRaycasts=false");
        }

        line(issues.Count == 0, label + "·可交互",
            issues.Count == 0
                ? "interactable=true，父级链全部激活，无 CanvasGroup 拦截"
                : string.Join("；", issues));
    }

    /// <summary>
    /// 射线会不会在点表单时误建标记。运行时行为没法在编辑期触发，
    /// 只能静态读源码确认那两道保护还在（被误删的表现就是：点按钮的同时背后又冒一个标记）。
    /// </summary>
    static void CheckRaycastUiConflict(System.Action<bool, string, string> line)
    {
        string path = Application.dataPath + "/Scripts/InspectionRaycaster.cs";
        if (!File.Exists(path))
        {
            line(false, "射线/UI 冲突防护", "找不到 " + path);
            return;
        }

        string src = File.ReadAllText(path);
        bool hasGuard = src.Contains("IsPointerOverGameObject");
        bool hasFormLock = src.Contains("IsFormOpen");
        bool hasMouseDown = src.Contains("GetMouseButtonDown");

        line(hasGuard, "射线/UI 冲突防护",
            hasGuard
                ? "含 EventSystem.IsPointerOverGameObject() 判断 → 点 UI 时不会放标记"
                : "缺少 IsPointerOverGameObject() 判断 → 点表单按钮会在背后同时放一个标记");

        line(hasFormLock && hasMouseDown, "表单打开时锁定点击",
            "IsFormOpen=" + hasFormLock + " / GetMouseButtonDown=" + hasMouseDown
            + "（两者都要有，否则表单打开期间点场景会重复放标记）");
    }

    /// <summary>
    /// 后端地址：空值 / 协议 / 端口 / 占位地址 / 与当前运行环境是否匹配，逐项查。
    ///
    /// 原则：只报告，绝不静默改写。
    /// 特别强调：不能为了消除 "Insecure connection not allowed" 就把 http 偷偷改成 https ——
    /// 本地 Go 后端默认只监听 http，改 https 会直接连不上。正确做法是
    /// Player Settings 允许 HTTP 明文（菜单「允许 HTTP 明文请求（本地后端）」）。
    /// </summary>
    static void CheckServerUrl(System.Action<bool, string, string> line, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            line(false, "后端地址", "为空，UnityWebRequest 会直接失败");
            return;
        }

        string u = raw.Trim();

        // 1) 协议
        bool isHttps = u.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
        bool isHttp = u.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
        {
            line(false, "后端地址·协议", "「" + u + "」缺少 http:// 或 https:// 前缀，请求会直接失败");
            return;
        }
        line(true, "后端地址·协议", isHttps ? "https" : "http");

        // 2) https 是否用错了地方（只提示，不改）
        if (isHttps)
        {
            bool localHttps = u.Contains("127.0.0.1") || u.Contains("localhost");
            line(!localHttps, "后端地址·https 适用性",
                localHttps
                    ? "https 指向本机：本地 Go 后端默认只提供 http，这样填连不上。"
                      + "若确实没配 TLS，请改回 http + 用「允许 HTTP 明文请求」菜单，不要靠改协议绕过报错"
                    : "https + 远程地址，请确认后端确实配了 TLS 证书");
        }

        // 3) 端口
        int schemeEnd = u.IndexOf("//", System.StringComparison.Ordinal) + 1;
        int lastColon = u.LastIndexOf(':');
        bool hasPort = lastColon > schemeEnd + 1;
        line(hasPort, "后端地址·端口",
            hasPort ? u.Substring(lastColon + 1).TrimEnd('/') + "（Go 后端监听 8080）"
                    : "未写端口（默认 80），与 Go 后端的 8080 不一致");

        // 4) 占位地址
        bool placeholder = u.Contains("192.168.1.100") || u.Contains("your-server") || u.Contains("example.com");
        line(!placeholder, "后端地址·占位地址",
            placeholder ? "「" + u + "」是教程示例地址，不是真实服务地址" : "非占位地址");

        // 5) 运行环境
        var target = EditorUserBuildSettings.activeBuildTarget;
        bool mobile = target == BuildTarget.Android || target == BuildTarget.iOS;
        bool loopback = u.Contains("127.0.0.1") || u.Contains("localhost");
        if (mobile)
        {
            line(!loopback, "后端地址·运行环境",
                "构建目标 " + target + "（真机）：127.0.0.1/localhost 指的是手机自己，连不到电脑，"
                + "需填电脑的局域网 IP，如 http://192.168.x.x:8080");
        }
        else
        {
            line(loopback, "后端地址·运行环境",
                "构建目标 " + target + "（本机/编辑器）：应为 127.0.0.1 或 localhost");
        }
    }

    /// <summary>递归按名字找子物体（包含未激活的）。</summary>
    static Transform FindDeep(Transform root, string name)
    {
        foreach (Transform t in root)
        {
            if (t.name == name) return t;
            var found = FindDeep(t, name);
            if (found != null) return found;
        }
        return null;
    }

    // =====================================================================
    //  子物体构造
    // =====================================================================

    static void AddLabel(string name, GameObject panel, string text, TMP_FontAsset font, float y)
    {
        var go = AddText(name, panel.transform, text, 23, font,
            TextAlignmentOptions.MidlineLeft, C_LABEL, FontStyles.Normal);
        SetRect(go, 0, y, CONTENT_W, 34, new Vector2(0.5f, 0.5f));
    }

    /// <summary>输入框：底色 + 描边 + TMP 输入组件 + Placeholder。</summary>
    static TMP_InputField AddInput(string name, GameObject panel, string placeholder,
        TMP_FontAsset font, int size, float y, float h, bool multiline)
    {
        var go = NewUI(name, panel.transform);
        SetRect(go, 0, y, CONTENT_W, h, new Vector2(0.5f, 0.5f));

        var bg = go.AddComponent<Image>();
        bg.sprite = RoundedSprite(BOX_RADIUS);
        bg.type = Image.Type.Sliced;
        bg.color = C_BOX_BG;

        // 细描边：用「只有边缘不透明」的 9-slice 贴图，中间透明才不会盖住底色填充
        var border = NewUI("Border", go.transform);
        StretchRect(border, 0, 0, 0, 0);
        var borderImg = border.AddComponent<Image>();
        borderImg.sprite = RoundedOutlineSprite(BOX_RADIUS);
        borderImg.type = Image.Type.Sliced;
        borderImg.color = C_BOX_LINE;
        borderImg.raycastTarget = false;
        border.transform.SetAsFirstSibling();

        // 文本区（带 RectMask2D，超出部分裁掉，多行时才能滚动）
        var viewport = NewUI("Text Area", go.transform);
        StretchRect(viewport, 16, 8, -16, -8);
        viewport.AddComponent<RectMask2D>();

        var textGo = NewUI("Text", viewport.transform);
        StretchRect(textGo, 0, 0, 0, 0);
        var textComp = textGo.AddComponent<TextMeshProUGUI>();
        textComp.font = font;
        textComp.fontSize = size;
        textComp.color = C_TITLE;
        textComp.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
        textComp.raycastTarget = false;

        var phGo = NewUI("Placeholder", viewport.transform);
        StretchRect(phGo, 0, 0, 0, 0);
        var ph = phGo.AddComponent<TextMeshProUGUI>();
        ph.font = font;
        ph.fontSize = size;
        ph.color = C_HINT;
        ph.alignment = textComp.alignment;
        ph.fontStyle = FontStyles.Italic;
        ph.text = placeholder;
        ph.raycastTarget = false;

        var input = go.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = viewport.GetComponent<RectTransform>();
        input.textComponent = textComp;
        input.placeholder = ph;
        input.pointSize = size;
        input.fontAsset = font;
        input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
        input.characterLimit = multiline ? 500 : 100;

        // Placeholder 要排在 Text 之前渲染，否则会盖在已输入的文字上
        phGo.transform.SetAsFirstSibling();

        return input;
    }

    /// <summary>只读信息框（显示命中坐标）。</summary>
    static TMP_Text AddInfoBox(string name, GameObject panel, string text,
        TMP_FontAsset font, int size, float y, float h)
    {
        var go = NewUI(name, panel.transform);
        SetRect(go, 0, y, CONTENT_W, h, new Vector2(0.5f, 0.5f));
        var bg = go.AddComponent<Image>();
        bg.sprite = RoundedSprite(BOX_RADIUS);
        bg.type = Image.Type.Sliced;
        bg.color = C_POS_BG;
        bg.raycastTarget = false;

        var label = AddText("Value", go.transform, text, size, font,
            TextAlignmentOptions.MidlineLeft, C_VALUE, FontStyles.Normal);
        StretchRect(label, 16, 0, -16, 0);
        return label.GetComponent<TMP_Text>();
    }

    /// <summary>TMP 下拉框（含展开用的 Template 结构，缺一个都会在运行时点开报错）。</summary>
    static TMP_Dropdown AddDropdown(string name, GameObject panel, TMP_FontAsset font, float y, float h)
    {
        var go = NewUI(name, panel.transform);
        SetRect(go, 0, y, CONTENT_W, h, new Vector2(0.5f, 0.5f));

        var bg = go.AddComponent<Image>();
        bg.sprite = RoundedSprite(BOX_RADIUS);
        bg.type = Image.Type.Sliced;
        bg.color = C_BOX_BG;

        // 显示当前选项的文字
        var captionGo = NewUI("Label", go.transform);
        StretchRect(captionGo, 18, 4, -44, -4);
        var caption = captionGo.AddComponent<TextMeshProUGUI>();
        caption.font = font;
        caption.fontSize = 20;
        caption.color = C_LABEL;
        caption.alignment = TextAlignmentOptions.MidlineLeft;
        caption.raycastTarget = false;

        // 右侧箭头（用图片三角形，不依赖字体字形，避免 SDF 图集缺字）
        var arrowGo = NewUI("Arrow", go.transform);
        SetRect(arrowGo, -26, 0, 16, 10, new Vector2(0.5f, 0.5f));
        var arrowImg = arrowGo.AddComponent<Image>();
        arrowImg.sprite = TriangleSprite();
        arrowImg.color = C_HINT;
        arrowImg.raycastTarget = false;

        // ---- 展开列表的 Template ----
        var template = NewUI("Template", go.transform);
        var trt = template.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, 2f);
        trt.sizeDelta = new Vector2(0f, 240f);
        var tBg = template.AddComponent<Image>();
        tBg.sprite = RoundedSprite(BOX_RADIUS);
        tBg.type = Image.Type.Sliced;
        tBg.color = Color.white;
        var scroll = template.AddComponent<ScrollRect>();

        var viewport = NewUI("Viewport", template.transform);
        StretchRect(viewport, 0, 0, 0, 0);
        viewport.AddComponent<Image>();
        viewport.AddComponent<RectMask2D>();

        var content = NewUI("Content", viewport.transform);
        var crt = content.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0.5f, 1f);
        crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = new Vector2(0f, 44f);

        // 单条选项：Toggle + Background + Checkmark 占位 + Label
        var item = NewUI("Item", content.transform);
        var irt = item.GetComponent<RectTransform>();
        irt.anchorMin = new Vector2(0f, 0.5f);
        irt.anchorMax = new Vector2(1f, 0.5f);
        irt.pivot = new Vector2(0.5f, 0.5f);
        irt.anchoredPosition = Vector2.zero;
        irt.sizeDelta = new Vector2(0f, 44f);

        var itemBg = NewUI("Item Background", item.transform);
        StretchRect(itemBg, 0, 0, 0, 0);
        var ibgImg = itemBg.AddComponent<Image>();
        ibgImg.sprite = RoundedSprite(8);
        ibgImg.type = Image.Type.Sliced;
        ibgImg.color = new Color(1f, 1f, 1f, 0.02f);

        var itemLabelGo = NewUI("Item Label", item.transform);
        StretchRect(itemLabelGo, 18, 2, -46, -2);
        var itemLabel = itemLabelGo.AddComponent<TextMeshProUGUI>();
        itemLabel.font = font;
        itemLabel.fontSize = 20;
        itemLabel.color = C_LABEL;
        itemLabel.alignment = TextAlignmentOptions.MidlineLeft;
        itemLabel.raycastTarget = false;

        // 选中标记：TMP_Dropdown 用 itemImage 作为 checkmark，不能拿背景图顶替
        var checkGo = NewUI("Item Checkmark", item.transform);
        var checkRt = checkGo.GetComponent<RectTransform>();
        checkRt.anchorMin = new Vector2(1f, 0.5f);
        checkRt.anchorMax = new Vector2(1f, 0.5f);
        checkRt.pivot = new Vector2(1f, 0.5f);
        checkRt.anchoredPosition = new Vector2(-18f, 0f);
        checkRt.sizeDelta = new Vector2(18f, 18f);
        var checkImg = checkGo.AddComponent<Image>();
        checkImg.sprite = RoundedSprite(9);
        checkImg.type = Image.Type.Sliced;
        checkImg.color = C_PRIMARY;
        checkImg.raycastTarget = false;

        var toggle = item.AddComponent<Toggle>();
        toggle.targetGraphic = ibgImg;
        toggle.graphic = checkImg;

        scroll.content = crt;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        var dropdown = go.AddComponent<TMP_Dropdown>();
        dropdown.targetGraphic = bg;
        dropdown.template = trt;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.itemImage = checkImg;
        dropdown.captionImage = null;

        template.SetActive(false); // 必须默认关闭，展开时才由 TMP 实例化

        // 默认选项：低 / 中 / 高，选「中」
        dropdown.ClearOptions();
        dropdown.AddOptions(new List<string> { "低", "中", "高" });
        dropdown.value = 1;
        dropdown.RefreshShownValue();

        return dropdown;
    }

    static Button AddButton(string name, GameObject panel, string text, TMP_FontAsset font,
        int size, Color32 bgColor, Color32 textColor, float x, float y, float w, float h)
    {
        var go = NewUI(name, panel.transform);
        SetRect(go, x, y, w, h, new Vector2(0.5f, 0.5f));

        var img = go.AddComponent<Image>();
        img.sprite = RoundedSprite(BOX_RADIUS);
        img.type = Image.Type.Sliced;
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        // 按下的手感：颜色变暗一点点
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
        colors.pressedColor = new Color(0.84f, 0.84f, 0.84f, 1f);
        colors.fadeDuration = 0.08f;
        btn.colors = colors;

        var label = AddText("Text", go.transform, text, size, font,
            TextAlignmentOptions.Center, textColor, FontStyles.Bold);
        StretchRect(label, 0, 0, 0, 0);

        return btn;
    }

    static GameObject AddText(string name, Transform parent, string content, int size,
        TMP_FontAsset font, TextAlignmentOptions align, Color color, FontStyles style)
    {
        var go = NewUI(name, parent);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = content;
        tmp.font = font;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.fontStyle = style;
        tmp.alignment = align;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return go;
    }

    // =====================================================================
    //  基础工具
    // =====================================================================

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void SetRect(GameObject go, float x, float y, float w, float h, Vector2 anchor)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>四边拉伸，参数是 left / bottom / right / top 的内缩像素。</summary>
    static void StretchRect(GameObject go, float left, float bottom, float right, float top)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(right, top);
    }

    static Canvas FindOverlayCanvas()
    {
        foreach (var c in Object.FindObjectsOfType<Canvas>())
            if (c.renderMode == RenderMode.ScreenSpaceOverlay) return c;
        return null;
    }

    // =====================================================================
    //  程序化生成 9-slice 圆角 / 三角形贴图
    //  （不引入外部美术资源，尺寸随 Image 缩放，边缘不变形）
    // =====================================================================

    static Sprite RoundedSprite(float radius)
    {
        int r = Mathf.RoundToInt(radius);
        string file = SPRITE_DIR + "/RoundedRect_" + r + ".png";
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(file);
        if (existing != null) return existing;

        int size = r * 2 + 4; // 中间留 4px 供九宫格拉伸
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, r, size - r);
                float cy = Mathf.Clamp(y + 0.5f, r, size - r);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                float a = Mathf.Clamp01(r - d + 0.5f); // 1px 抗锯齿过渡
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();

        return WriteSprite(file, tex, new Vector4(r, r, r, r));
    }

    /// <summary>只有边缘 1.5px 不透明的圆角描边贴图，中间透明，叠在填充层上做细边框。</summary>
    static Sprite RoundedOutlineSprite(float radius)
    {
        int r = Mathf.RoundToInt(radius);
        string file = SPRITE_DIR + "/RoundedOutline_" + r + ".png";
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(file);
        if (existing != null) return existing;

        int size = r * 2 + 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 圆角矩形的有符号距离场（<0 在内部）
                float qx = Mathf.Abs(x + 0.5f - half) - (half - r);
                float qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                float ox = Mathf.Max(qx, 0f);
                float oy = Mathf.Max(qy, 0f);
                float sd = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;

                float a = Mathf.Clamp01((1.5f - Mathf.Abs(sd)) / 0.75f); // 描边 1.5px + 抗锯齿
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();

        return WriteSprite(file, tex, new Vector4(r, r, r, r));
    }

    static Sprite TriangleSprite()
    {
        string file = SPRITE_DIR + "/ArrowDown.png";
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(file);
        if (existing != null) return existing;

        const int size = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 倒三角形：顶点在上边中轴，底边在下
                float halfWidth = (1f - (float)y / (size - 1)) * (size * 0.5f);
                float dx = Mathf.Abs(x + 0.5f - size * 0.5f);
                float a = Mathf.Clamp01(halfWidth - dx + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();

        return WriteSprite(file, tex, Vector4.zero);
    }

    static Sprite WriteSprite(string assetPath, Texture2D tex, Vector4 border)
    {
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "UI"));
        File.WriteAllBytes(assetPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.spriteBorder = border;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }
}
