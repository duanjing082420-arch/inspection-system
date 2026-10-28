using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 巡检场景分步搭建器。每一步一个菜单项，点一次只做一步，做完自动存盘。
///   第1步：Tools ▸ 巡检场景 ▸ 第1步 地面与墙体
///   第2步：Tools ▸ 巡检场景 ▸ 第2步 机柜与柜门指示灯
///   第3步：Tools ▸ 巡检场景 ▸ 第3步 相机归位与视角控制
///   第4步：Tools ▸ 巡检场景 ▸ 第4步 清理重复相机控制器
///   第5步：Tools ▸ 巡检场景 ▸ 第5步 水泵机组与材质
///   第6步：Tools ▸ 巡检场景 ▸ 第6步 完善水泵（联轴器+防护罩+材质）
///   第7步：Tools ▸ 巡检场景 ▸ 第7步 主水管与支管
///   第8步：Tools ▸ 巡检场景 ▸ 第8步 阀门法兰与警示标签
///   第9步：Tools ▸ 巡检场景 ▸ 第9步 复制水泵 B/C
///   第10步：Tools ▸ 巡检场景 ▸ 第10步 整理 Hierarchy（FactoryScene）
///   第11步：Tools ▸ 巡检场景 ▸ 第11步 支管接到三台泵
///   第12步：Tools ▸ 巡检场景 ▸ 第12步 设备巡检标识 InspectionTarget
///   第13步：Tools ▸ 巡检场景 ▸ 第13步 后端服务 InspectionServices
/// </summary>
public static class SceneLayoutBuilder
{
    // =====================================================================
    //  第 1 步：地面 + 三面墙
    // =====================================================================
    [MenuItem("Tools/巡检场景/第1步 地面与墙体", false, 100)]
    public static void Step1_FloorAndWalls()
    {
        if (GuardPlaying()) return;

        // 1. Ground
        GameObject ground = FindByName("Ground");
        if (ground == null) { LogErr("[Step1] 找不到 Ground，已中止。"); return; }

        MeshFilter mf = ground.GetComponent<MeshFilter>();
        bool isPlane = mf != null && mf.sharedMesh != null && mf.sharedMesh.name.Contains("Plane");

        RecordTransform(ground.transform, "Step1 Ground");
        ground.transform.position = Vector3.zero;
        ground.transform.rotation = Quaternion.identity;
        ground.transform.localScale = isPlane
            ? new Vector3(2f, 1f, 1.4f)     // Plane 默认 10x10 -> 20x14
            : new Vector3(20f, 1f, 14f);    // Cube 直接给实际尺寸
        Debug.Log($"[Step1] Ground 类型={(isPlane ? "Plane" : "Cube")}，Scale={ground.transform.localScale}");

        // 2. 后墙（场景里现有那个，名字是小写 wall）
        GameObject backWall = FindByName("wall") ?? FindByName("Wall");
        if (backWall == null) { LogErr("[Step1] 找不到 wall / Wall，已中止。"); return; }
        RecordTransform(backWall.transform, "Step1 BackWall");
        backWall.transform.position = new Vector3(0f, 2.5f, 7f);
        backWall.transform.rotation = Quaternion.identity;
        backWall.transform.localScale = new Vector3(20f, 5f, 0.2f);

        // 3 & 4. 两侧墙
        EnsureCube("Wall_Left",  new Vector3(-10f, 2.5f, 0f), new Vector3(0.2f, 5f, 14f));
        EnsureCube("Wall_Right", new Vector3( 10f, 2.5f, 0f), new Vector3(0.2f, 5f, 14f));

        SaveActiveScene();
        Debug.Log("[Step1] 地面与三面墙完成，场景已保存。");
    }

    // =====================================================================
    //  第 2 步：电气机柜区（机柜 A~D + 柜门 + 指示灯）
    // =====================================================================
    [MenuItem("Tools/巡检场景/第2步 机柜与柜门指示灯", false, 110)]
    public static void Step2_Cabinets()
    {
        if (GuardPlaying()) return;

        Vector3 cabScale = new Vector3(1.4f, 2.4f, 0.8f);

        // 第1~2小步：定位/复制四个机柜
        SetupCabinet("Cabinet_A", new Vector3(-4.0f, 1.2f, 2.0f), cabScale);
        SetupCabinet("Cabinet_B", new Vector3(-1.8f, 1.2f, 2.0f), cabScale);
        SetupCabinet("Cabinet_C", new Vector3( 3.0f, 1.2f, 2.0f), cabScale);
        SetupCabinet("Cabinet_D", new Vector3( 5.2f, 1.2f, 2.0f), cabScale);

        // 第4小步：材质（绿色 / 红色）
        Material green = GetOrCreateMat("Mat_GreenLight", Color.green);
        Material red   = GetOrCreateMat("Mat_RedLight",   Color.red);

        // 第3~5小步：给每个柜子加柜门 + 两盏灯
        string[] cabinets = { "Cabinet_A", "Cabinet_B", "Cabinet_C", "Cabinet_D" };
        foreach (string c in cabinets)
        {
            GameObject cab = FindByName(c);
            if (cab == null) { LogErr($"[Step2] {c} 不存在，跳过。"); continue; }
            AddCabinetChildren(cab, green, red);
        }

        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step2] 四个机柜 + 柜门 + 指示灯完成，场景已保存。");
    }

    // =====================================================================
    //  第 3 步：相机归位 + 挂视角控制器
    // =====================================================================
    [MenuItem("Tools/巡检场景/第3步 相机归位与视角控制", false, 120)]
    public static void Step3_CameraSetup()
    {
        if (GuardPlaying()) return;

        GameObject camGo = FindByName("Main Camera");
        Camera cam = camGo != null ? camGo.GetComponent<Camera>() : Camera.main;
        if (cam == null) { LogErr("[Step3] 找不到 Main Camera。"); return; }

        RecordTransform(cam.transform, "Step3 Camera");
        cam.transform.position = new Vector3(0f, 1.8f, -6.2f);
        cam.transform.rotation = Quaternion.identity;

        InspectionCamera ic = cam.GetComponent<InspectionCamera>();
        if (ic == null)
        {
            ic = Undo.AddComponent<InspectionCamera>(cam.gameObject);
            Debug.Log("[Step3] 已给 Main Camera 添加 InspectionCamera 组件");
        }

        InspectionRaycaster ray = cam.GetComponent<InspectionRaycaster>();
        if (ray != null) ic.raycaster = ray;

        EditorUtility.SetDirty(cam);
        SaveActiveScene();
        Debug.Log("[Step3] 相机归位到 (0, 1.8, -6.2)，Play 后可用 WASD / QE / 右键拖动。");
    }

    // =====================================================================
    //  第 4 步：清理重复的相机控制器
    // =====================================================================
    [MenuItem("Tools/巡检场景/第4步 清理重复相机控制器", false, 130)]
    public static void Step4_CleanupCameraControllers()
    {
        if (GuardPlaying()) return;

        GameObject camGo = FindByName("Main Camera");
        if (camGo == null) { LogErr("[Step4] 找不到 Main Camera。"); return; }

        // 教程里那个 InspectionCameraController 被重复挂了 4 份，每份都在自己的 Update 里
        // 位移一次 —— 按一下 W 实际走 5 倍距离，镜头直接飞出厂房。
        // 这里全部清掉，只保留 InspectionCamera（有表单锁 + 边界限制 + 自动归位）。
        int removed = 0;
        foreach (var c in camGo.GetComponents<InspectionCameraController>())
        {
            Undo.DestroyObjectImmediate(c);
            removed++;
        }

        // 自己的控制器也只留一份
        var mine = camGo.GetComponents<InspectionCamera>();
        for (int i = 1; i < mine.Length; i++)
        {
            Undo.DestroyObjectImmediate(mine[i]);
            removed++;
        }

        if (mine.Length == 0)
        {
            Undo.AddComponent<InspectionCamera>(camGo);
            Debug.Log("[Step4] Main Camera 上没有 InspectionCamera，已补挂一个。");
        }

        EditorUtility.SetDirty(camGo);
        SaveActiveScene();
        Debug.Log("[Step4] 已清理 " + removed + " 个多余控制器组件。" +
                  "Main Camera 现在只剩 1 个 InspectionCamera，场景已保存。");
    }

    // =====================================================================
    //  第 5 步：水泵机组（设备总节点 + 5 个零件 + 4 个材质）
    // =====================================================================
    [MenuItem("Tools/巡检场景/第5步 水泵机组与材质", false, 140)]
    public static void Step5_PumpUnit()
    {
        if (GuardPlaying()) return;

        // --- 1) 设备总节点（空物体）：整体移动设备时只动它，零件跟着走 ---
        GameObject unit = FindByName("Pump_Unit_A");
        if (unit == null)
        {
            unit = new GameObject("Pump_Unit_A");
            Undo.RegisterCreatedObjectUndo(unit, "Create Pump_Unit_A");
            Debug.Log("[Step5] 新建设备总节点 Pump_Unit_A");
        }
        else
        {
            RecordTransform(unit.transform, "Set Pump_Unit_A");
        }
        unit.transform.position = new Vector3(0f, 0f, -1f);
        unit.transform.rotation = Quaternion.identity;
        unit.transform.localScale = Vector3.one;

        // --- 2) 材质：灰（金属）/ 蓝（泵体）/ 浅灰（管路）/ 黄（警示，先备着） ---
        Material metal   = GetOrCreateMat("Mat_Metal",   new Color(0.62f, 0.63f, 0.66f));
        Material pump    = GetOrCreateMat("Mat_Pump",    new Color(0.18f, 0.42f, 0.82f));
        Material pipe    = GetOrCreateMat("Mat_Pipe",    new Color(0.80f, 0.81f, 0.84f));
        Material warning = GetOrCreateMat("Mat_Warning", new Color(1.00f, 0.78f, 0.10f));
        Debug.Log("[Step5] 材质就绪：" + metal.name + " / " + pump.name + " / " + pipe.name
                  + " / " + warning.name + "（黄暂不赋给任何物体，留给后续警示标识）");

        // =================================================================
        //  几何基准（很重要，教程给的那组 Y 值算出来是悬空的）：
        //    Unity 内置 Cylinder 的真实尺寸 = 高 2、半径 0.5（不是 1！）
        //    => scale.y = S 时高度 = 2S；scale.x/z = D 时直径 = D、半径 = D/2
        //    Cube = 边长 1，所以 scale = 实际尺寸
        //  底座顶面 = 0.3（中心 0.15 + 半高 0.15）
        //  所有零件都要「底面正好落在 0.3」，否则一悬空、一穿模。
        // =================================================================
        const float BASE_TOP = 0.3f;

        // --- 3) 底座：中心 y=0.15、厚 0.3 → 底面正好落在 y=0 地面上 ---
        // 底面与地面 Plane 完全共面会 Z-fighting（闪条纹），往下埋 1cm 规避。
        GameObject baseGo = EnsureChild(unit, "Pump_Base", PrimitiveType.Cube);
        SetLocal(baseGo, new Vector3(0f, 0.14f, 0f), Vector3.zero, new Vector3(3f, 0.3f, 1.2f));
        SetSharedMat(baseGo, metal);

        // --- 4) 电机主体：Cylinder 绕 Z 转 90°，从「沿 Y 竖立」变成「沿 X 横躺」 ---
        // 横躺后半径方向是 Y：半径 = 0.48/2 = 0.24 → 中心 y = 0.3 + 0.24 = 0.54
        const float MOTOR_Y = BASE_TOP + 0.48f * 0.5f;   // 0.54
        GameObject motor = EnsureChild(unit, "Motor_Body", PrimitiveType.Cylinder);
        SetLocal(motor, new Vector3(-0.55f, MOTOR_Y, 0f), new Vector3(0f, 0f, 90f),
            new Vector3(0.48f, 0.85f, 0.48f));
        SetSharedMat(motor, metal);
        Debug.Log($"[Step5] Motor_Body 中心 y={MOTOR_Y:F2}，底面 y={MOTOR_Y - 0.24f:F2}（应为 {BASE_TOP}）");

        // --- 5) 电机前端连接盘：与电机同轴，y 必须和电机一致，否则错位 ---
        GameObject front = EnsureChild(unit, "Motor_Front", PrimitiveType.Cylinder);
        SetLocal(front, new Vector3(0.32f, MOTOR_Y, 0f), new Vector3(0f, 0f, 90f),
            new Vector3(0.4f, 0.12f, 0.4f));
        SetSharedMat(front, metal);

        // --- 6) 水泵主体：高 = 0.6*2 = 1.2、半高 0.6 → 中心 y = 0.3 + 0.6 = 0.9 ---
        const float HOUSING_H = 0.6f * 2f;                // 1.2
        const float HOUSING_Y = BASE_TOP + HOUSING_H * 0.5f;  // 0.9
        GameObject housing = EnsureChild(unit, "Pump_Housing", PrimitiveType.Cylinder);
        SetLocal(housing, new Vector3(1.05f, HOUSING_Y, 0f), Vector3.zero, new Vector3(0.65f, 0.6f, 0.65f));
        SetSharedMat(housing, pump);
        Debug.Log($"[Step5] Pump_Housing 中心 y={HOUSING_Y:F2}，底部 y={HOUSING_Y - HOUSING_H * 0.5f:F2}（应为 {BASE_TOP}）");

        // --- 7) 出水口：坐在泵体顶面上（顶面 = 0.9 + 0.6 = 1.5），高 1.0、半高 0.5 → 中心 2.0 ---
        float housingTop = HOUSING_Y + HOUSING_H * 0.5f;  // 1.5
        GameObject outlet = EnsureChild(unit, "Pump_Outlet_Pipe", PrimitiveType.Cylinder);
        SetLocal(outlet, new Vector3(1.05f, housingTop + 0.5f, 0f), Vector3.zero,
            new Vector3(0.16f, 0.5f, 0.16f));
        SetSharedMat(outlet, pipe);

        // --- 8) 进水口：横向接管，轴线对齐泵体中段（y = 泵体中心 0.9） ---
        GameObject inlet = EnsureChild(unit, "Pump_Inlet_Pipe", PrimitiveType.Cylinder);
        SetLocal(inlet, new Vector3(1.65f, HOUSING_Y, 0f), new Vector3(0f, 0f, 90f),
            new Vector3(0.16f, 0.55f, 0.16f));
        SetSharedMat(inlet, pipe);

        EditorUtility.SetDirty(unit);
        SaveActiveScene();
        AssetDatabase.SaveAssets();

        Debug.Log("[Step5] 水泵机组完成：Pump_Unit_A(0,0,-1) + 底座/电机/前端盘/泵体/进出水管，场景已保存。" +
                  "点击任意零件，表单「巡检对象」都会显示 Pump_Unit_A。");
    }

    // =====================================================================
    //  第 6 步：完善水泵（联轴器 + 防护罩 + 工业材质）
    // =====================================================================
    [MenuItem("Tools/巡检场景/第6步 完善水泵（联轴器+防护罩+材质）", false, 150)]
    public static void Step6_PumpDetails()
    {
        if (GuardPlaying()) return;

        GameObject unit = FindByName("Pump_Unit_A");
        if (unit == null) { LogErr("[Step6] 找不到 Pump_Unit_A，请先执行第5步。"); return; }

        // 1.4 工业材质
        Material motor = GetOrCreateMat("Mat_Motor", new Color(0.22f, 0.28f, 0.38f)); // 深蓝灰
        Material pump = GetOrCreateMat("Mat_Pump", new Color(0.13f, 0.35f, 0.72f));  // 工业蓝
        Material metal = GetOrCreateMat("Mat_Metal", new Color(0.62f, 0.63f, 0.66f)); // 金属灰
        Material warning = GetOrCreateMat("Mat_Warning", new Color(1.00f, 0.78f, 0.10f)); // 警示黄

        // 沿用第5步的几何基准：底座顶面 0.3，电机轴线 y = 0.3 + 0.24 = 0.54
        const float MOTOR_Y = 0.54f;

        // --- 1.2 联轴器 ---
        // 教程给 y=0.85，但电机顶面只有 0.54 + 0.24 = 0.78，放 0.85 会整根悬在电机上方。
        // 联轴器必须和电机轴同轴，所以取电机轴线高度 0.54。
        GameObject coupling = EnsureChild(unit, "Motor_Coupling", PrimitiveType.Cylinder);
        SetLocal(coupling, new Vector3(0.48f, MOTOR_Y, 0f), new Vector3(0f, 0f, 90f),
            new Vector3(0.16f, 0.18f, 0.16f));
        SetSharedMat(coupling, metal);
        Debug.Log($"[Step6] Motor_Coupling 与电机同轴 y={MOTOR_Y}（教程的 0.85 会悬空，已下移）");

        // --- 1.3 防护罩 ---
        // Cube 的 scale 就是实际尺寸：高 0.48 → 中心取 0.54 时底面 = 0.30，正好坐在底座顶面上。
        GameObject guard = EnsureChild(unit, "Pump_Guard", PrimitiveType.Cube);
        SetLocal(guard, new Vector3(0.45f, MOTOR_Y, 0f), Vector3.zero,
            new Vector3(0.65f, 0.48f, 0.65f));
        SetSharedMat(guard, warning);
        Debug.Log($"[Step6] Pump_Guard 底面 y={MOTOR_Y - 0.24f}（贴底座顶面 0.3），正好罩住联轴器");

        // --- 1.4 材质按用途重新分配 ---
        AssignMat(unit, "Motor_Body", motor);
        AssignMat(unit, "Motor_Front", motor);
        AssignMat(unit, "Pump_Housing", pump);
        AssignMat(unit, "Pump_Base", metal);
        AssignMat(unit, "Pump_Inlet_Pipe", metal);
        AssignMat(unit, "Pump_Outlet_Pipe", metal);

        EditorUtility.SetDirty(unit);
        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step6] 联轴器 + 防护罩 + 工业材质完成，场景已保存。");
    }

    // =====================================================================
    //  第 7 步：主水管 / 支管 / 弯头 / 水泵连接管
    // =====================================================================
    [MenuItem("Tools/巡检场景/第7步 主水管与支管", false, 160)]
    public static void Step7_PipeSystem()
    {
        if (GuardPlaying()) return;

        GameObject sys = FindByName("Pipe_System_A");
        if (sys == null)
        {
            sys = new GameObject("Pipe_System_A");
            Undo.RegisterCreatedObjectUndo(sys, "Create Pipe_System_A");
            Debug.Log("[Step7] 新建管路总节点 Pipe_System_A");
        }
        else
        {
            RecordTransform(sys.transform, "Set Pipe_System_A");
        }
        sys.transform.position = Vector3.zero;
        sys.transform.rotation = Quaternion.identity;
        sys.transform.localScale = Vector3.one;

        Material metal = GetOrCreateMat("Mat_Metal", new Color(0.62f, 0.63f, 0.66f));

        // --- 2.1 主水管：绕 Z 转 90° → 沿 X 横躺；scale.y=7 ⇒ 长 14，直径 0.12 ---
        GameObject main = EnsureChild(sys, "Pipe_Main_01", PrimitiveType.Cylinder);
        SetLocal(main, new Vector3(0f, 3.4f, -3f), new Vector3(0f, 0f, 90f),
            new Vector3(0.12f, 7f, 0.12f));
        SetSharedMat(main, metal);
        Debug.Log("[Step7] Pipe_Main_01 沿 X 轴，x ∈ [-7, 7]，管中心 y=3.4");

        // --- 2.2 竖向支管：scale.y=0.85 ⇒ 高 1.7，中心 2.55 ⇒ 顶端 3.40 与主管中心齐平 ---
        GameObject br1 = EnsureChild(sys, "Pipe_Branch_Pump", PrimitiveType.Cylinder);
        SetLocal(br1, new Vector3(0f, 2.55f, -3f), Vector3.zero, new Vector3(0.10f, 0.85f, 0.10f));
        SetSharedMat(br1, metal);

        GameObject br2 = EnsureChild(sys, "Pipe_Branch_Right", PrimitiveType.Cylinder);
        SetLocal(br2, new Vector3(4f, 2.55f, -3f), Vector3.zero, new Vector3(0.10f, 0.85f, 0.10f));
        SetSharedMat(br2, metal);
        Debug.Log("[Step7] 支管 y ∈ [1.70, 3.40]，顶端与主管中心齐平，底端 y=1.70");

        // --- 2.3 弯头：球心放在真正的交汇处 y=3.4（教程的 3.0 会落在支管中段） ---
        GameObject el1 = EnsureChild(sys, "Pipe_Elbow_A", PrimitiveType.Sphere);
        SetLocal(el1, new Vector3(0f, 3.4f, -3f), Vector3.zero, new Vector3(0.22f, 0.22f, 0.22f));
        SetSharedMat(el1, metal);

        GameObject el2 = EnsureChild(sys, "Pipe_Elbow_B", PrimitiveType.Sphere);
        SetLocal(el2, new Vector3(4f, 3.4f, -3f), Vector3.zero, new Vector3(0.22f, 0.22f, 0.22f));
        SetSharedMat(el2, metal);

        // --- 2.4 让支管真正接到水泵 ---
        // 教程只给了一段 scale.y=0.5（长 1.0）的 Z 向管，z ∈ [-2.5, -1.5]，离水泵所在的 z=-1 差 0.5，
        // 而且 x=0.5 和出水管 x=1.05 也对不上 → 两头都不接。
        // 这里用两段接实：
        //   ① 沿 X：从支管底 (0, 1.7, -3) 走到 (1.05, 1.7, -3)
        //   ② 沿 Z：从 (1.05, 1.7, -3) 走到 (1.05, 1.7, -1)，正好落在 Pump_Outlet_Pipe 上
        GameObject cx = EnsureChild(sys, "Pipe_Pump_Connector_X", PrimitiveType.Cylinder);
        SetLocal(cx, new Vector3(0.525f, 1.7f, -3f), new Vector3(0f, 0f, 90f),
            new Vector3(0.10f, 0.525f, 0.10f));
        SetSharedMat(cx, metal);

        GameObject conn = EnsureChild(sys, "Pipe_Pump_Connector", PrimitiveType.Cylinder);
        SetLocal(conn, new Vector3(1.05f, 1.7f, -2f), new Vector3(90f, 0f, 0f),
            new Vector3(0.10f, 1.0f, 0.10f));
        SetSharedMat(conn, metal);
        Debug.Log("[Step7] 连接管两段：X 段 x ∈ [0, 1.05] + Z 段 z ∈ [-3, -1]，终点落在出水管上");

        EditorUtility.SetDirty(sys);
        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step7] 管路系统完成（主管/两条支管/两个弯头/两段连接管），场景已保存。");
    }

    // =====================================================================
    //  第 8 步：手轮阀门 + 法兰 + 警示标签
    // =====================================================================
    [MenuItem("Tools/巡检场景/第8步 阀门法兰与警示标签", false, 170)]
    public static void Step8_ValveAndFlanges()
    {
        if (GuardPlaying()) return;

        GameObject sys = FindByName("Pipe_System_A");
        if (sys == null) { LogErr("[Step8] 找不到 Pipe_System_A，请先执行第7步。"); return; }

        Material metal = GetOrCreateMat("Mat_Metal", new Color(0.62f, 0.63f, 0.66f));
        Material red = GetOrCreateMat("Mat_ValveRed", new Color(0.85f, 0.12f, 0.12f));
        Material warning = GetOrCreateMat("Mat_Warning", new Color(1.00f, 0.78f, 0.10f));

        // --- 3.1 阀门总节点（挂在 Pipe_System_A 下，用局部坐标） ---
        GameObject valve = NewEmptyChild(sys, "Valve_A");
        SetLocal(valve, new Vector3(0f, 2.8f, -3f), Vector3.zero, Vector3.one);

        // --- 3.2 阀体 + 阀杆 ---
        // 阀体竖直圆柱：高 0.36、直径 0.22，中心在支管上（支管 y ∈ [1.70, 3.40]）
        GameObject body = EnsureChild(valve, "Valve_Body", PrimitiveType.Cylinder);
        SetLocal(body, Vector3.zero, Vector3.zero, new Vector3(0.22f, 0.18f, 0.22f));
        SetSharedMat(body, metal);

        GameObject stem = EnsureChild(valve, "Valve_Stem", PrimitiveType.Cylinder);
        SetLocal(stem, new Vector3(0f, 0.28f, 0f), Vector3.zero, new Vector3(0.055f, 0.15f, 0.055f));
        SetSharedMat(stem, metal);

        // --- 3.3 红色手轮 ---
        // 阀杆竖直朝上（沿 Y），所以手轮必须是**水平圆盘**：圆柱不旋转就是水平盘。
        // 教程给的 Rotation(90,0,0) 会把盘立起来，和竖直阀杆互相矛盾（等于轮子装在竖向轴上）。
        GameObject rim = EnsureChild(valve, "Valve_Wheel_Rim", PrimitiveType.Cylinder);
        SetLocal(rim, new Vector3(0f, 0.48f, 0f), Vector3.zero,
            new Vector3(0.28f, 0.035f, 0.28f));
        SetSharedMat(rim, red);

        // 辐条躺在水平面（XZ）里，一根沿 X、一根沿 Z，组成十字
        GameObject sx = EnsureChild(valve, "Valve_Spoke_X", PrimitiveType.Cube);
        SetLocal(sx, new Vector3(0f, 0.48f, 0f), Vector3.zero,
            new Vector3(0.48f, 0.045f, 0.045f));
        SetSharedMat(sx, red);

        GameObject sz = EnsureChild(valve, "Valve_Spoke_Z", PrimitiveType.Cube);
        SetLocal(sz, new Vector3(0f, 0.48f, 0f), Vector3.zero,
            new Vector3(0.045f, 0.045f, 0.48f));
        SetSharedMat(sz, red);
        Debug.Log("[Step8] 手轮水平（与竖直阀杆垂直），两根辐条在 XZ 平面内交叉");

        // --- 3.4 法兰：绕 Z 转 90° 与主管同轴，直径 0.24 > 主管 0.12 ---
        GameObject fa = EnsureChild(sys, "Pipe_Flange_A", PrimitiveType.Cylinder);
        SetLocal(fa, new Vector3(-3f, 3.4f, -3f), new Vector3(0f, 0f, 90f),
            new Vector3(0.24f, 0.07f, 0.24f));
        SetSharedMat(fa, metal);

        GameObject fb = EnsureChild(sys, "Pipe_Flange_B", PrimitiveType.Cylinder);
        SetLocal(fb, new Vector3(3f, 3.4f, -3f), new Vector3(0f, 0f, 90f),
            new Vector3(0.24f, 0.07f, 0.24f));
        SetSharedMat(fb, metal);

        // --- 3.5 警示标签：贴在防护罩朝向相机的一侧 ---
        GameObject pumpUnit = FindByName("Pump_Unit_A");
        if (pumpUnit != null)
        {
            // 防护罩前面在局部 z = -0.325，标签厚 0.025，放 -0.35 刚好贴在外面不打架
            GameObject label = EnsureChild(pumpUnit, "Pump_Warning_Label", PrimitiveType.Cube);
            SetLocal(label, new Vector3(0.45f, 0.54f, -0.35f), Vector3.zero,
                new Vector3(0.42f, 0.22f, 0.025f));
            SetSharedMat(label, warning);
            EditorUtility.SetDirty(pumpUnit);
            Debug.Log("[Step8] 警示标签贴在防护罩正面（局部 z=-0.35），y 与防护罩中心对齐");
        }
        else
        {
            LogErr("[Step8] 找不到 Pump_Unit_A，跳过警示标签。");
        }

        EditorUtility.SetDirty(sys);
        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step8] 阀门 + 法兰 + 警示标签完成，场景已保存。");
    }

    // =====================================================================
    //  第 9 步：复制水泵机组 B / C
    // =====================================================================
    [MenuItem("Tools/巡检场景/第9步 复制水泵 B/C", false, 180)]
    public static void Step9_ClonePumps()
    {
        if (GuardPlaying()) return;

        GameObject src = FindByName("Pump_Unit_A");
        if (src == null) { LogErr("[Step9] 找不到 Pump_Unit_A，请先执行第5步。"); return; }

        CloneUnit(src, "Pump_Unit_B", new Vector3(-5f, 0f, -1f));
        CloneUnit(src, "Pump_Unit_C", new Vector3(5f, 0f, -1f));

        SaveActiveScene();
        Debug.Log("[Step9] 三套泵机就位：A(0,0,-1) / B(-5,0,-1) / C(5,0,-1)，场景已保存。" +
                  "复制件与机柜（z=2）不在同一深度，不会互相穿插。");
    }

    /// <summary>复制整个设备节点（子物体、材质、层级一起带过来），只改父对象的位置。</summary>
    static void CloneUnit(GameObject src, string name, Vector3 pos)
    {
        GameObject dst = FindByName(name);
        if (dst == null)
        {
            dst = Object.Instantiate(src);
            dst.name = name;
            Undo.RegisterCreatedObjectUndo(dst, "Clone " + name);
            Debug.Log("[Step9] 复制出 " + name);
        }
        else
        {
            RecordTransform(dst.transform, "Set " + name);
        }
        dst.transform.SetParent(src.transform.parent, true);
        dst.transform.position = pos;
        dst.transform.rotation = Quaternion.identity;
        dst.transform.localScale = Vector3.one;
    }

    // =====================================================================
    //  第 10 步：整理 Hierarchy（归入 FactoryScene）
    // =====================================================================
    [MenuItem("Tools/巡检场景/第10步 整理 Hierarchy（FactoryScene）", false, 190)]
    public static void Step10_GroupHierarchy()
    {
        if (GuardPlaying()) return;

        GameObject root = FindByName("FactoryScene");
        if (root == null)
        {
            root = new GameObject("FactoryScene");
            Undo.RegisterCreatedObjectUndo(root, "Create FactoryScene");
        }
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        // 相机 / UI / 灯光绝不能动：
        //   相机挂了射线和控制器脚本；Canvas 一旦被挪进设备父节点会跟着设备走；
        //   EventSystem 和灯光同理。所以这几样留在根级。
        string[] skip = { "Main Camera", "Canvas", "EventSystem", "Directional Light", "FactoryScene" };

        int moved = 0;
        foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
        {
            if (go.transform.parent != null) continue;              // 只收根级物体
            if (System.Array.IndexOf(skip, go.name) >= 0) continue; // 跳过相机/UI/灯光
            Undo.SetTransformParent(go.transform, root.transform, "Group " + go.name);
            moved++;
        }

        EditorUtility.SetDirty(root);
        SaveActiveScene();
        Debug.Log($"[Step10] 已把 {moved} 个根级物体归入 FactoryScene（相机 / UI / 灯光保持原位），场景已保存。");
    }

    // =====================================================================
    //  第 11 步：把主管真正接到三台泵（重建支管）
    // =====================================================================
    [MenuItem("Tools/巡检场景/第11步 支管接到三台泵", false, 200)]
    public static void Step11_ConnectPipesToPumps()
    {
        if (GuardPlaying()) return;

        GameObject sys = FindByName("Pipe_System_A");
        if (sys == null) { LogErr("[Step11] 找不到 Pipe_System_A，请先执行第7步。"); return; }

        Material metal = GetOrCreateMat("Mat_Metal", new Color(0.62f, 0.63f, 0.66f));

        // --- 1) 先拆掉悬空的旧件 ---
        // 教程的支管/连接管是按「只有一台泵、且泵在 x=0」写的：
        //   Pipe_Branch_Pump 在 x=0，垂直下到 y=1.7 就断了；
        //   Pipe_Branch_Right 在 x=4，正下方是空的（C 泵在 x=5）；
        //   Pipe_Pump_Connector 那两段只服务 A 泵，B/C 完全没有管路。
        // 所以这一组全部删掉，按每台泵自己的出水口位置重下。
        string[] obsolete =
        {
            "Pipe_Branch_Pump", "Pipe_Branch_Right",
            "Pipe_Pump_Connector", "Pipe_Pump_Connector_X",
            "Pipe_Elbow_A", "Pipe_Elbow_B"
        };
        int removed = 0;
        foreach (string n in obsolete)
        {
            Transform t = sys.transform.Find(n);
            if (t != null)
            {
                Undo.DestroyObjectImmediate(t.gameObject);
                removed++;
            }
        }
        Debug.Log($"[Step11] 已拆除 {removed} 个位置错误的旧支管 / 连接管 / 弯头");

        // --- 2) 每台泵建一套「竖管 + 弯头 + 横管」 ---
        // 关键数字（都是世界坐标，Pipe_System_A 本身在原点）：
        //   主管中心 y = 3.40
        //   泵出水口在泵位 X 的 +1.05 处，出水口顶面 y = 2.50
        //   => 竖管从 3.40 垂到 2.50，长 0.90，中心 2.95
        //   => 横管沿 Z 从 -3 走到 -1，长 2.00，中心 z = -2
        const float HEADER_Y = 3.4f;
        const float OUTLET_X_OFFSET = 1.05f;
        const float OUTLET_TOP_Y = 2.5f;
        const float PIPE_D = 0.10f;

        float riserLen = HEADER_Y - OUTLET_TOP_Y;          // 0.90
        float riserScaleY = riserLen * 0.5f;               // 0.45
        float riserY = (HEADER_Y + OUTLET_TOP_Y) * 0.5f;   // 2.95

        string[] sfx = { "A", "B", "C" };
        float[] unitX = { 0f, -5f, 5f };

        for (int i = 0; i < sfx.Length; i++)
        {
            float px = unitX[i] + OUTLET_X_OFFSET;   // 出水口世界 X

            // 竖管
            GameObject riser = EnsureChild(sys, "Pipe_Riser_" + sfx[i], PrimitiveType.Cylinder);
            SetLocal(riser, new Vector3(px, riserY, -3f), Vector3.zero,
                new Vector3(PIPE_D, riserScaleY, PIPE_D));
            SetSharedMat(riser, metal);

            // 弯头（球心放在真正的转角：竖管底 2.50 与横管起点 z=-3 的交点）
            GameObject joint = EnsureChild(sys, "Pipe_Joint_" + sfx[i], PrimitiveType.Sphere);
            SetLocal(joint, new Vector3(px, OUTLET_TOP_Y, -3f), Vector3.zero,
                new Vector3(0.14f, 0.14f, 0.14f));
            SetSharedMat(joint, metal);

            // 横管：沿 Z 走，末端 z=-1 正好插进出水口（出水口是竖直圆柱，半径 0.08，
            // 横管端面落在它的轴线上，两者必然交叠 0.08，是真接上而不是贴边）
            GameObject run = EnsureChild(sys, "Pipe_Run_" + sfx[i], PrimitiveType.Cylinder);
            SetLocal(run, new Vector3(px, OUTLET_TOP_Y, -2f), new Vector3(90f, 0f, 0f),
                new Vector3(PIPE_D, 1.0f, PIPE_D));
            SetSharedMat(run, metal);

            Debug.Log($"[Step11] {sfx[i]} 泵：竖管 x={px:F2} y∈[2.50,3.40]，横管 y=2.50 z∈[-3,-1]，" +
                      $"末端接出水口 ({px:F2}, 2.50, -1.00)");
        }

        // --- 3) 阀门跟着挪到 A 泵的横管上 ---
        // 原来 Valve_A 在 (0, 2.8, -3)，是挂在 x=0 那根支管上的；支管一删它就悬空。
        // 挪到 A 泵横管 (1.05, 2.5, -2)：阀体竖直圆柱横跨管道，阀杆朝上，手轮在 y≈2.98，
        // 低于主管 3.34，不会打架。
        Transform vt = sys.transform.Find("Valve_A");
        if (vt != null)
        {
            SetLocal(vt.gameObject, new Vector3(OUTLET_X_OFFSET, OUTLET_TOP_Y, -2f),
                Vector3.zero, Vector3.one);

            // 顺手把旧手轮摆正：阀杆竖直，手轮必须是水平圆盘。
            // 教程的 Rotation(90,0,0) 让盘立着，和竖直阀杆矛盾。
            Transform rimT = vt.Find("Valve_Wheel_Rim");
            if (rimT != null)
            {
                SetLocal(rimT.gameObject, new Vector3(0f, 0.48f, 0f), Vector3.zero,
                    new Vector3(0.28f, 0.035f, 0.28f));
            }

            // 旧的竖/横辐条方向也是错的，连同名字一起换成 X / Z 两根
            foreach (string old in new[] { "Valve_Spoke_Vertical", "Valve_Spoke_Horizontal" })
            {
                Transform o = vt.Find(old);
                if (o != null) Undo.DestroyObjectImmediate(o.gameObject);
            }

            Material red = GetOrCreateMat("Mat_ValveRed", new Color(0.85f, 0.12f, 0.12f));
            GameObject sx = EnsureChild(vt.gameObject, "Valve_Spoke_X", PrimitiveType.Cube);
            SetLocal(sx, new Vector3(0f, 0.48f, 0f), Vector3.zero,
                new Vector3(0.48f, 0.045f, 0.045f));
            SetSharedMat(sx, red);

            GameObject sz = EnsureChild(vt.gameObject, "Valve_Spoke_Z", PrimitiveType.Cube);
            SetLocal(sz, new Vector3(0f, 0.48f, 0f), Vector3.zero,
                new Vector3(0.045f, 0.045f, 0.48f));
            SetSharedMat(sz, red);

            Debug.Log("[Step11] Valve_A 已挪到 A 泵横管 (1.05, 2.50, -2)，手轮改为水平，辐条重建为 X/Z 十字");
        }
        else
        {
            Debug.LogWarning("[Step11] 没找到 Valve_A，跳过阀门挪位（可先跑第8步）。");
        }

        EditorUtility.SetDirty(sys);
        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step11] 三台泵全部接上主管，场景已保存。");
    }

    // =====================================================================
    //  第12步：给每台设备挂 InspectionTarget（中文显示名 + 稳定设备 ID）
    //
    //  分工：零件负责被射线点中，设备根节点负责显示名称和归属。
    //  InspectionTarget.Resolve() 会从命中的零件逐级往上找，最终返回设备根节点上的组件，
    //  所以点 Motor_Body 表单显示「水泵机组 A」，点 Cabinet_Door 显示「控制柜 A」。
    // =====================================================================
    [MenuItem("Tools/巡检场景/第12步 设备巡检标识 InspectionTarget", false, 210)]
    public static void Step12_InspectionTargets()
    {
        if (GuardPlaying()) return;

        // 区域按现场实际分工划分：泵与阀门在一号泵房，控制柜集中在配电区。
        // 有了区域，巡检记录才能按「谁负责哪一片」来分发，这也是列表/React 的筛选维度之一。
        const string ZONE_PUMP = "一号泵房";
        const string ZONE_POWER = "配电区";

        AttachTarget("Pump_Unit_A", "水泵机组 A", "PUMP-A", ZONE_PUMP);
        AttachTarget("Pump_Unit_B", "水泵机组 B", "PUMP-B", ZONE_PUMP);
        AttachTarget("Pump_Unit_C", "水泵机组 C", "PUMP-C", ZONE_PUMP);
        AttachTarget("Valve_A", "主水管阀门 A", "VALVE-A", ZONE_PUMP);
        AttachTarget("Cabinet_A", "控制柜 A", "CABINET-A", ZONE_POWER);
        AttachTarget("Cabinet_B", "控制柜 B", "CABINET-B", ZONE_POWER);
        AttachTarget("Cabinet_C", "控制柜 C", "CABINET-C", ZONE_POWER);
        AttachTarget("Cabinet_D", "控制柜 D", "CABINET-D", ZONE_POWER);

        SaveActiveScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[Step12] 8 台设备的巡检标识已配置并保存。");
    }

    /// <summary>给设备上标识组件并写入显示名 / ID / 区域（幂等：已有组件就只改值，不会重复挂）。</summary>
    static void AttachTarget(string objectName, string displayName, string targetId, string zone)
    {
        GameObject go = FindByName(objectName);
        if (go == null)
        {
            Debug.LogWarning("[Step12] 场景里找不到 " + objectName +
                "，跳过（先跑对应的建场景步骤再来）。");
            return;
        }

        InspectionTarget t = go.GetComponent<InspectionTarget>();
        if (t == null)
        {
            t = go.AddComponent<InspectionTarget>();
            Undo.RegisterCreatedObjectUndo(t, "Add InspectionTarget");
        }

        // displayName / targetId / owner 都是 private [SerializeField]，
        // 编辑器脚本里不能直接赋值，走序列化写入最稳（不依赖字段可见性）。
        SerializedObject so = new SerializedObject(t);
        SerializedProperty pName = so.FindProperty("displayName");
        SerializedProperty pId = so.FindProperty("targetId");
        SerializedProperty pZone = so.FindProperty("zone");
        SerializedProperty pOwner = so.FindProperty("owner");
        if (pName == null || pId == null)
        {
            Debug.LogError("[Step12] InspectionTarget 上找不到 displayName/targetId 字段，请确认脚本已编译。");
            return;
        }
        pName.stringValue = displayName;
        pId.stringValue = targetId;
        if (pZone != null) pZone.stringValue = zone;   // 旧版本脚本没有这个字段，找不到就跳过，不报错
        if (pOwner != null) pOwner.objectReferenceValue = null;  // Owner 留空：它就是设备根
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(go);
        Debug.Log("[Step12] " + objectName + " → " + displayName + " / " + targetId);
    }

    // =====================================================================
    //  第13步：创建唯一的后端服务对象 InspectionServices
    //
    //  通信层只能有一份。挂多份的后果是一次提交发好几个请求、后端出现重复记录，
    //  而且排查时只会看到「记录莫名翻倍」，很难联想到是组件重复。
    //  这里保证场景里恰好一个 InspectionApiClient，并把地址纠成本机 8080。
    // =====================================================================
    [MenuItem("Tools/巡检场景/第13步 后端服务 InspectionServices", false, 220)]
    public static void Step13_ApiService()
    {
        if (GuardPlaying()) return;

        const string SERVICES_NAME = "InspectionServices";
        const string DEFAULT_URL = "http://127.0.0.1:8080/api/issues";

        GameObject services = FindByName(SERVICES_NAME);
        if (services == null)
        {
            services = new GameObject(SERVICES_NAME);
            Undo.RegisterCreatedObjectUndo(services, "Create InspectionServices");
            Debug.Log("[Step13] 已创建 " + SERVICES_NAME + "（放在根层级，不要塞进设备父节点）。");
        }

        // 1) 清掉散落的重复实例（例如误挂到 Main Camera 或某台设备上的）
        InspectionApiClient[] all = Object.FindObjectsOfType<InspectionApiClient>(true);
        int removed = 0;
        foreach (InspectionApiClient c in all)
        {
            if (c == null) continue;
            if (c.gameObject == services) continue;

            Debug.LogWarning("[Step13] 移除多余的 InspectionApiClient（挂在 " + c.gameObject.name + " 上）");
            Object.DestroyImmediate(c);
            removed++;
        }

        // 2) 确保服务对象上有一份
        InspectionApiClient client = services.GetComponent<InspectionApiClient>();
        if (client == null)
        {
            client = services.AddComponent<InspectionApiClient>();
            Undo.RegisterCreatedObjectUndo(client, "Add InspectionApiClient");
        }

        // 3) apiUrl 是 private [SerializeField]，必须走序列化写入
        SerializedObject so = new SerializedObject(client);
        SerializedProperty pUrl = so.FindProperty("apiUrl");
        if (pUrl == null)
        {
            Debug.LogError("[Step13] InspectionApiClient 上找不到 apiUrl 字段，请确认脚本已编译。");
            return;
        }

        string old = pUrl.stringValue;
        if (string.IsNullOrWhiteSpace(old) || old.Contains("192.168.1.100"))
        {
            pUrl.stringValue = DEFAULT_URL;
            Debug.Log("[Step13] apiUrl 从「" + (string.IsNullOrWhiteSpace(old) ? "空" : old) +
                      "」改为 " + DEFAULT_URL);
        }
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(services);

        SaveActiveScene();
        AssetDatabase.SaveAssets();

        Debug.Log("[Step13] 后端服务对象就绪：" + services.name +
                  "，apiUrl = " + pUrl.stringValue +
                  (removed > 0 ? "，已清理 " + removed + " 个重复组件" : "，无重复组件") +
                  "。场景已保存。");
    }

    // =====================================================================
    //  工具方法
    // =====================================================================

    /// <summary>给指定子物体赋材质；找不到就报错，不静默跳过。</summary>
    static void AssignMat(GameObject parent, string childName, Material mat)
    {
        Transform t = parent.transform.Find(childName);
        if (t == null) { LogErr("[材质] 找不到子物体 " + childName + "，跳过上色。"); return; }
        SetSharedMat(t.gameObject, mat);
    }

    /// <summary>在 parent 下找/建一个空物体节点（用作设备或部件的总节点）。</summary>
    static GameObject NewEmptyChild(GameObject parent, string name)
    {
        Transform t = parent.transform.Find(name);
        if (t != null) return t.gameObject;

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        return go;
    }

    /// <summary>设置子物体的局部 Transform（父节点移动时零件跟着走，不会跑偏）。</summary>
    static void SetLocal(GameObject go, Vector3 pos, Vector3 euler, Vector3 scale)
    {
        RecordTransform(go.transform, "Set " + go.name);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
    }

    static bool GuardPlaying()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("先退出 Play 模式", "请先停止运行，再执行搭建。", "好");
            return true;
        }
        return false;
    }

    static void SaveActiveScene()
    {
        UnityEngine.SceneManagement.Scene scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    static void LogErr(string msg) => Debug.LogError(msg);

    static GameObject FindByName(string name)
    {
        GameObject go = GameObject.Find(name);
        return go != null ? go : null;
    }

    static void RecordTransform(Transform t, string undoName)
    {
        Undo.RecordObject(t, undoName);
    }

    static GameObject EnsureCube(string name, Vector3 pos, Vector3 scale)
    {
        GameObject go = FindByName(name);
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Debug.Log($"[Step1] 新建 {name}");
        }
        else
        {
            RecordTransform(go.transform, "Set " + name);
            Debug.Log($"[Step1] 复用已存在的 {name}");
        }
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = scale;
        return go;
    }

    /// <summary>定位/创建单个机柜（含 A/B 复用、C/D 新建）。</summary>
    static GameObject SetupCabinet(string name, Vector3 pos, Vector3 scale)
    {
        GameObject go = FindByName(name);
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Debug.Log($"[Step2] 新建机柜 {name}");
        }
        else
        {
            RecordTransform(go.transform, "Set " + name);
        }
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = scale;
        return go;
    }

    /// <summary>给一个柜子添加（或复用）柜门 + 两盏指示灯，并上色。</summary>
    static void AddCabinetChildren(GameObject cab, Material green, Material red)
    {
        // 柜门
        GameObject door = EnsureChild(cab, "Cabinet_Door", PrimitiveType.Cube);
        door.transform.localPosition = new Vector3(0f, 0f, -0.41f);
        door.transform.localRotation = Quaternion.identity;
        door.transform.localScale = new Vector3(0.9f, 0.9f, 0.03f);

        // 绿灯
        GameObject gl = EnsureChild(cab, "Green_Light", PrimitiveType.Sphere);
        gl.transform.localPosition = new Vector3(-0.35f, 0.75f, -0.44f);
        gl.transform.localRotation = Quaternion.identity;
        gl.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
        SetSharedMat(gl, green);

        // 红灯
        GameObject rl = EnsureChild(cab, "Red_Light", PrimitiveType.Sphere);
        rl.transform.localPosition = new Vector3(-0.15f, 0.75f, -0.44f);
        rl.transform.localRotation = Quaternion.identity;
        rl.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
        SetSharedMat(rl, red);
    }

    /// <summary>在 parent 下找/建一个子物体；新建后设为子物体并登记 Undo。</summary>
    static GameObject EnsureChild(GameObject parent, string name, PrimitiveType type)
    {
        Transform existing = parent.transform.Find(name);
        if (existing != null) return existing.gameObject;

        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false); // false=按局部坐标接管
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        return go;
    }

    static void SetSharedMat(GameObject go, Material mat)
    {
        MeshRenderer r = go.GetComponent<MeshRenderer>();
        if (r == null) return;
        r.sharedMaterial = mat;
    }

    /// <summary>取或建一个 Standard 材质并存盘到 Assets/Materials。</summary>
    static Material GetOrCreateMat(string matName, Color color)
    {
        string dir = "Assets/Materials";
        string path = dir + "/" + matName + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets", "Materials");
            mat = new Material(Shader.Find("Standard"));
            mat.color = color;
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"[Step2] 新建材质 {matName}");
        }
        else
        {
            mat.color = color;
            EditorUtility.SetDirty(mat);
        }
        return mat;
    }
}
