// InspectionWiring.cs
//
// 「第14步」接线菜单：把场景里各组件之间的引用一次性补齐并保存。
//
// 为什么要这一步：IssueFormController.apiClient 目前在场景里是空的。
// 运行时代码里有兜底（EnsureApiClient 会 FindObjectOfType 找 InspectionServices），
// 所以功能上不会挂，但 Inspector 里看着是空的，排查问题时容易误判成「没接后端」。
// 这一步把引用真正写进场景文件，并把结果打印出来。
//
// 菜单：Tools ▸ 巡检 ▸ 接线：表单 ↔ 后端通信（第14步）

using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class InspectionWiring
{
    // =====================================================================
    //  修复 UI 缩放
    // ---------------------------------------------------------------------
    //  症状：Play 后面板巨大、字巨大、表单显示不全。
    //  根因：CanvasScaler 的参考分辨率是 750x1334（手机竖屏），
    //        而 MatchWidthOrHeight = 0（**只按宽度匹配**）。
    //        match=0 时 缩放 = 屏幕宽 / 750 —— PC 上 1920 宽就是 2.56 倍，
    //        于是 660x930 的表单被画成 1690x2381 像素（屏幕才 1080 高），
    //        上下被裁掉一半，26 号字也变成 67px。
    //  修法：参考分辨率改成 1920x1080（PC 演示的实际分辨率），match 改成 0.5（宽高混合）。
    //        这样 1920x1080 下缩放正好是 1，「1 单位 = 1 像素」，其他分辨率等比缩放不会被裁。
    // =====================================================================
    [MenuItem("Tools/巡检/修复 UI 缩放（Canvas → 1920x1080）", false, 15)]
    static void FixCanvasScaler()
    {
        Canvas canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[巡检] 场景里没有 Canvas。");
            return;
        }

        CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null)
        {
            Debug.LogError("[巡检] Canvas 上没有 CanvasScaler 组件。");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("===== 修复 UI 缩放 =====");
        sb.AppendLine("修改前：模式=" + scaler.uiScaleMode
                      + " / 参考分辨率=" + scaler.referenceResolution
                      + " / match=" + scaler.matchWidthOrHeight);

        var so = new SerializedObject(scaler);

        SerializedProperty mode = so.FindProperty("m_UiScaleMode");
        SerializedProperty resolution = so.FindProperty("m_ReferenceResolution");
        SerializedProperty screenMatchMode = so.FindProperty("m_ScreenMatchMode");
        SerializedProperty match = so.FindProperty("m_MatchWidthOrHeight");

        if (mode != null) mode.enumValueIndex = 1;            // Scale With Screen Size
        if (resolution != null) resolution.vector2Value = new Vector2(1920f, 1080f);
        if (screenMatchMode != null) screenMatchMode.enumValueIndex = 0;  // Match Width Or Height
        if (match != null) match.floatValue = 0.5f;           // 宽高混合

        so.ApplyModifiedProperties();

        sb.AppendLine("修改后：模式=" + scaler.uiScaleMode
                      + " / 参考分辨率=" + scaler.referenceResolution
                      + " / match=" + scaler.matchWidthOrHeight);
        sb.AppendLine("Canvas 渲染模式=" + canvas.renderMode);
        sb.AppendLine("说明：1920x1080 下缩放 = 1，1 单位 = 1 像素；其他分辨率等比缩放，内容不会被裁。");
        sb.AppendLine("建议把 Game 视图分辨率设为 1920x1080（或 16:9）。");

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        sb.AppendLine("场景已保存：" + scene.path);

        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/巡检/接线：表单 ↔ 后端通信（第14步）", false, 14)]
    static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== 巡检接线检查 =====");

        // ---------- 1. 找场景里的表单控制器 ----------
        IssueFormController controller = null;
        foreach (var c in Resources.FindObjectsOfTypeAll<IssueFormController>())
        {
            if (EditorUtility.IsPersistent(c)) continue;          // 跳过 Project 里的预制体资源
            if (c.gameObject.scene.IsValid() == false) continue;  // 只认场景里的
            controller = c;
            break;
        }

        if (controller == null)
        {
            Debug.LogError("[巡检] 场景里没有 IssueFormController。请先执行 Tools ▸ 巡检 ▸ 创建上报面板 IssueFormPanel。");
            return;
        }

        sb.AppendLine("表单控制器挂在：" + controller.gameObject.name);

        // ---------- 2. 找 / 建 后端通信对象 ----------
        InspectionApiClient apiClient = null;
        foreach (var a in Resources.FindObjectsOfTypeAll<InspectionApiClient>())
        {
            if (EditorUtility.IsPersistent(a)) continue;
            if (a.gameObject.scene.IsValid() == false) continue;
            if (apiClient != null)
            {
                // 多份 ApiClient = 一次提交发多个请求，后端会出现重复记录，只保留第一份。
                Debug.LogWarning("[巡检] 场景里有多个 InspectionApiClient，已删除多余组件（保留 " +
                                 apiClient.gameObject.name + " 上的那份）。");
                Undo.DestroyObjectImmediate(a);
                continue;
            }
            apiClient = a;
        }

        if (apiClient == null)
        {
            GameObject services = new GameObject("InspectionServices");
            Undo.RegisterCreatedObjectUndo(services, "创建 InspectionServices");
            apiClient = Undo.AddComponent<InspectionApiClient>(services);
            sb.AppendLine("已新建 InspectionServices 并挂上 InspectionApiClient");
        }
        else
        {
            sb.AppendLine("后端通信对象：" + apiClient.gameObject.name);
        }

        // 地址兜底：示例地址 / 空值 / 旧通道统一改成本机地址（不改协议，本地后端就是 http）。
        // 旧通道 /api/inspections 也要改：提交与查询已合并到 /api/issues（嵌套 position），
        // 不改的话接线完仍然走老接口，抓包还是扁平 x/y/z。
        string url = apiClient.ApiUrl;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("192.168.1.100") || url.EndsWith("/api/inspections"))
            apiClient.ApiUrl = "http://127.0.0.1:8080/api/issues";

        sb.AppendLine("接口地址：" + apiClient.ApiUrl);

        // ---------- 3. 写进场景：表单 ↔ 通信 ----------
        var so = new SerializedObject(controller);
        SerializedProperty apiProp = so.FindProperty("apiClient");
        if (apiProp != null && !ReferenceEquals(apiProp.objectReferenceValue, apiClient))
        {
            apiProp.objectReferenceValue = apiClient;
            sb.AppendLine("已接线：IssueFormController.apiClient -> " + apiClient.gameObject.name);
        }
        else
        {
            sb.AppendLine("apiClient 引用已经正确，无需改动");
        }

        if (string.IsNullOrWhiteSpace(controller.serverUrl) || controller.serverUrl.Contains("192.168.1.100"))
        {
            so.FindProperty("serverUrl").stringValue = "http://127.0.0.1:8080";
            sb.AppendLine("serverUrl 已纠正为 http://127.0.0.1:8080");
        }

        so.ApplyModifiedProperties();

        // ---------- 4. 顺手补：射线脚本的 formController ----------
        foreach (var r in Resources.FindObjectsOfTypeAll<InspectionRaycaster>())
        {
            if (EditorUtility.IsPersistent(r)) continue;
            if (r.gameObject.scene.IsValid() == false) continue;

            var rso = new SerializedObject(r);
            SerializedProperty formProp = rso.FindProperty("formController");
            if (formProp != null && formProp.objectReferenceValue == null)
            {
                formProp.objectReferenceValue = controller;
                rso.ApplyModifiedProperties();
                sb.AppendLine("已接线：" + r.gameObject.name + ".formController -> " + controller.gameObject.name);
            }
        }

        // ---------- 5. 表单字段完整性 ----------
        sb.AppendLine("---- 表单字段 ----");
        sb.AppendLine("标题输入框   " + (controller.titleInput != null ? "已绑定" : "未绑定"));
        sb.AppendLine("描述输入框   " + (controller.descInput != null ? "已绑定" : "未绑定"));
        sb.AppendLine("优先级下拉   " + (controller.priorityDropdown != null ? "已绑定" : "未绑定"));
        sb.AppendLine("巡检对象文本 " + (controller.objectText != null ? "已绑定" : "未绑定"));
        sb.AppendLine("位置文本     " + (controller.positionText != null ? "已绑定" : "未绑定"));
        sb.AppendLine("状态文本     " + (controller.statusText != null ? "已绑定" : "未绑定"));
        sb.AppendLine("面板根节点   " + (controller.panelRoot != null ? "已绑定" : "未绑定"));
        sb.AppendLine("后端通信     " + (controller.apiClient != null ? "已绑定" : "未绑定"));
        sb.AppendLine("（按钮事件由 IssueFormController.Awake 在运行时按名字绑定，" +
                      "Inspector 的 On Click() 列表为空是正常现象）");

        // ---------- 6. 保存 ----------
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        sb.AppendLine("场景已保存：" + scene.path);

        Debug.Log(sb.ToString());
    }
}
