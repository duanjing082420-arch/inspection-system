// InspectionOutboxTools.cs
//
// 本地待发队列的查看 / 清空工具。
// 用途：提交失败后，记录会留在 Application.persistentDataPath/inspection_outbox.json，
//      下次启动自动重发。这两个菜单用来确认「到底存下来了没有」和「手动清掉不要的」。
//
// 菜单：Tools ▸ 巡检 ▸ 待重发记录：查看 / 清空
// 本文件必须放在 Assets/Editor 下（仅编辑器编译，不会打进真机包）。

using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class InspectionOutboxTools
{
    [MenuItem("Tools/巡检/待重发记录：查看", false, 20)]
    static void Show()
    {
        string path = InspectionApiClient.OutboxPath;

        if (!File.Exists(path))
        {
            Debug.Log("[巡检] 本地没有待重发记录。\n队列文件：" + path);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("===== 本地待重发巡检记录 =====");
        sb.AppendLine("文件：" + path);

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var file = JsonUtility.FromJson<InspectionOutboxFile>(text);

            if (file == null || file.items == null || file.items.Count == 0)
            {
                sb.AppendLine("当前 0 条。");
                Debug.Log(sb.ToString());
                return;
            }

            sb.AppendLine("共 " + file.items.Count + " 条：");
            for (int i = 0; i < file.items.Count; i++)
            {
                var p = file.items[i];
                if (p == null || p.record == null) continue;

                sb.AppendLine("  [" + (i + 1) + "] " + p.record.targetId +
                              " / " + p.record.title +
                              " / " + p.record.priority +
                              " / 入队时间 " + p.queuedAt +
                              " / 本地编号 " + p.clientId);
            }

            sb.AppendLine("下次进入 Play 会自动重发。确认不需要可执行「待重发记录：清空」。");
            Debug.Log(sb.ToString());
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[巡检] 读取队列失败：" + e.Message + "\n文件：" + path);
        }

        // 顺手在资源管理器里定位文件，方便直接打开看原始 JSON。
        // Windows 上 RevealInFinder 要的是反斜杠路径，斜杠会定位失败。
        EditorUtility.RevealInFinder(path.Replace('/', '\\'));
    }

    [MenuItem("Tools/巡检/待重发记录：清空", false, 21)]
    static void Clear()
    {
        string path = InspectionApiClient.OutboxPath;

        if (!File.Exists(path))
        {
            Debug.Log("[巡检] 本地没有待重发记录，无需清空。");
            return;
        }

        bool confirm = EditorUtility.DisplayDialog(
            "清空本地待重发记录",
            "将删除：\n" + path + "\n\n这些记录尚未提交到后端，删除后无法恢复。确定继续？",
            "删除",
            "取消");

        if (!confirm) return;

        InspectionApiClient.ClearPending();
    }
}
