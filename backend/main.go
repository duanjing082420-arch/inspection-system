package main

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"syscall"
	"time"
)

// 数据文件默认是当前目录下的 issues.json。
// 容器里用 INSPECTION_DATA_FILE 指到挂载的数据卷，否则容器一删数据就跟着没了 ——
// 本地直接跑时不用管这个变量，行为与之前完全一致。
var dataFile = "issues.json"

// 问题优先级 / 状态用英文枚举，三端（Unity、Go、React）共用一套契约。
// 前端只负责把 open/in_progress/resolved 显示成「待处理/处理中/已完成」。
const (
	PriorityLow    = "low"
	PriorityMedium = "medium"
	PriorityHigh   = "high"

	StatusOpen       = "open"
	StatusInProgress = "in_progress"
	StatusResolved   = "resolved"

	// 没有区域信息的历史记录统一归到这里，避免区域筛选出现空白分组
	DefaultZone = "未分区"
)

// Position 是 Unity 射线命中点在世界坐标系里的 x/y/z，必须和 Unity 端
// IssueFormController.PositionDto 的字段名一一对应（JsonUtility 按字段名序列化）。
type Position struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

type Issue struct {
	ID          string   `json:"id"`
	TargetID    string   `json:"targetId"`   // 设备唯一标识，如 PUMP-A / VALVE-A / CABINET-A
	TargetName  string   `json:"targetName"` // 设备显示名称，如 水泵机组 A
	Zone        string   `json:"zone"`       // 所属区域（一号泵房 / 配电区…）；空值由 normalize 兜底为「未分区」
	Title       string   `json:"title"`
	Description string   `json:"description"`
	Priority    string   `json:"priority"` // low | medium | high
	Position    Position `json:"position"` // 世界坐标（Unity hit.point）
	Status      string   `json:"status"`   // open | in_progress | resolved
	CreatedAt   string   `json:"createdAt"`
	UpdatedAt   string   `json:"updatedAt"`
}

type IssueStore struct {
	mu     sync.Mutex
	issues []Issue
	file   string
}

// normalize 兼容早期字段（location/level + 中文状态）的旧数据：
// 缺 priority/status/position 时补默认值，保证升级后历史记录不会显示成空白。
func (issue *Issue) normalize() {
	if issue.Priority == "" {
		issue.Priority = PriorityMedium
	}
	if issue.Status == "" {
		issue.Status = StatusOpen
	}
	switch issue.Status {
	case "待处理":
		issue.Status = StatusOpen
	case "处理中":
		issue.Status = StatusInProgress
	case "已完成":
		issue.Status = StatusResolved
	}
	switch issue.Priority {
	case "一般":
		issue.Priority = PriorityLow
	case "重要":
		issue.Priority = PriorityMedium
	case "紧急":
		issue.Priority = PriorityHigh
	}
	if issue.UpdatedAt == "" {
		issue.UpdatedAt = issue.CreatedAt
	}
	// 早期数据没有 zone，统一归到「未分区」，否则列表按区域筛会出现一个空白分组
	if strings.TrimSpace(issue.Zone) == "" {
		issue.Zone = DefaultZone
	}
}

func NewIssueStore(file string) (*IssueStore, error) {
	store := &IssueStore{
		issues: []Issue{},
		file:   file,
	}

	data, err := os.ReadFile(file)
	if err != nil {
		if os.IsNotExist(err) {
			return store, nil
		}
		return nil, err
	}

	if len(data) > 0 {
		if err := json.Unmarshal(data, &store.issues); err != nil {
			return nil, err
		}
	}

	if store.issues == nil {
		store.issues = []Issue{}
	}

	for i := range store.issues {
		store.issues[i].normalize()
	}

	return store, nil
}

func (s *IssueStore) saveLocked() error {
	data, err := json.MarshalIndent(s.issues, "", "  ")
	if err != nil {
		return err
	}

	tempFile := s.file + ".tmp"
	if err := os.WriteFile(tempFile, data, 0644); err != nil {
		return err
	}

	return os.Rename(tempFile, s.file)
}

// list 返回副本，按创建时间倒序（最新的排前面），便于管理端阅读。
func (s *IssueStore) list() []Issue {
	s.mu.Lock()
	defer s.mu.Unlock()

	result := make([]Issue, len(s.issues))
	copy(result, s.issues)

	sort.SliceStable(result, func(i, j int) bool {
		return result[i].CreatedAt > result[j].CreatedAt
	})
	return result
}

func (s *IssueStore) add(issue Issue) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.issues = append(s.issues, issue)
	if err := s.saveLocked(); err != nil {
		s.issues = s.issues[:len(s.issues)-1]
		return err
	}

	return nil
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)

	if err := json.NewEncoder(w).Encode(value); err != nil {
		log.Printf("返回 JSON 失败：%v", err)
	}
}

// withCORS 允许 React 开发服务器（localhost:5173）直接跨域调用本服务。
// Unity 客户端不受同源策略限制，但浏览器端必须有这些头，否则 preflight 会失败。
// withRecovery 兜住单个请求处理过程中的 panic，返回 500 而不是让整个进程崩掉。
// 任何一个 handler 里出现空指针、越界这类问题时，只影响这一个请求，
// 其余接口仍然可用 —— 否则一次意外就把服务整个带下线。
func withRecovery(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			recovered := recover()
			if recovered == nil {
				return
			}
			log.Printf("[panic] %s %s -> %v", r.Method, r.URL.Path, recovered)
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusInternalServerError)
			_ = json.NewEncoder(w).Encode(map[string]string{"error": "服务器内部错误"})
		}()
		next.ServeHTTP(w, r)
	})
}

func withCORS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
		w.Header().Set("Access-Control-Max-Age", "86400")

		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}

		next.ServeHTTP(w, r)
	})
}

func healthHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{
			"error": "只允许使用 GET 方法",
		})
		return
	}

	writeJSON(w, http.StatusOK, map[string]string{
		"status":    "ok",
		"message":   "巡检问题后端服务运行正常",
		"timestamp": time.Now().Format(time.RFC3339),
	})
}

func issuesHandler(store *IssueStore) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			items := store.list()
			writeJSON(w, http.StatusOK, map[string]any{
				"items": items,
				"total": len(items),
			})

		case http.MethodPost:
			r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
			defer r.Body.Close()

			decoder := json.NewDecoder(r.Body)
			decoder.DisallowUnknownFields()

			// 接口契约与 Unity 端 IssueFormController.IssuePayload 严格对齐。
			// 注意：这里开了 DisallowUnknownFields，Unity 每新增一个字段都必须同步加进来，
			// 否则提交会被打成 400「请求 JSON 格式不正确」。
			var input struct {
				TargetID    string   `json:"targetId"`
				TargetName  string   `json:"targetName"`
				Zone        string   `json:"zone"`
				Title       string   `json:"title"`
				Description string   `json:"description"`
				Priority    string   `json:"priority"`
				Position    Position `json:"position"`
				CreatedAt   string   `json:"createdAt"`
			}

			if err := decoder.Decode(&input); err != nil {
				writeJSON(w, http.StatusBadRequest, map[string]string{
					"error": "请求 JSON 格式不正确：" + err.Error(),
				})
				return
			}

			input.Title = strings.TrimSpace(input.Title)
			input.Description = strings.TrimSpace(input.Description)
			input.Priority = strings.TrimSpace(input.Priority)

			// 标题必填：Unity 端也拦了一道，这里做服务端兜底。
			if input.Title == "" {
				writeJSON(w, http.StatusBadRequest, map[string]string{
					"error": "问题标题不能为空",
				})
				return
			}

			if input.Priority == "" {
				input.Priority = PriorityMedium
			}
			if input.Priority != PriorityLow &&
				input.Priority != PriorityMedium &&
				input.Priority != PriorityHigh {
				writeJSON(w, http.StatusBadRequest, map[string]string{
					"error": "priority 只能是 low、medium 或 high",
				})
				return
			}

			now := time.Now()

			// 创建时间：客户端传了合法的 ISO 8601 就用客户端的，否则以服务端为准。
			createdAt := now.Format(time.RFC3339)
			if t := strings.TrimSpace(input.CreatedAt); t != "" {
				if parsed, err := time.Parse(time.RFC3339, t); err == nil {
					createdAt = parsed.Format(time.RFC3339)
				} else {
					log.Printf("客户端 createdAt 格式无法解析（%q），改用服务端时间", t)
				}
			}

			// 区域允许为空：老客户端不传也能提交，落库时 normalize 会兜成「未分区」
			zone := strings.TrimSpace(input.Zone)
			if zone == "" {
				zone = DefaultZone
			}

			issue := Issue{
				ID:          now.Format("20060102150405.000000000"),
				TargetID:    strings.TrimSpace(input.TargetID),
				TargetName:  strings.TrimSpace(input.TargetName),
				Zone:        zone,
				Title:       input.Title,
				Description: input.Description,
				Priority:    input.Priority,
				Position:    input.Position,
				Status:      StatusOpen,
				CreatedAt:   createdAt,
				UpdatedAt:   createdAt,
			}

			if err := store.add(issue); err != nil {
				log.Printf("保存巡检问题失败：%v", err)
				writeJSON(w, http.StatusInternalServerError, map[string]string{
					"error": "问题保存失败",
				})
				return
			}

			writeJSON(w, http.StatusCreated, map[string]any{
				"message": "巡检问题创建成功",
				"item":    issue,
			})

		default:
			w.Header().Set("Allow", "GET, POST")
			writeJSON(w, http.StatusMethodNotAllowed, map[string]string{
				"error": "此接口只支持 GET 和 POST 方法",
			})
		}
	}
}

func deleteIssueHandler(store *IssueStore) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodDelete {
			w.Header().Set("Allow", "DELETE")
			writeJSON(w, http.StatusMethodNotAllowed, map[string]string{
				"error": "此接口只支持 DELETE 方法",
			})
			return
		}

		// 从 /api/issues/{id} 中提取问题编号。
		id := strings.TrimPrefix(r.URL.Path, "/api/issues/")
		id = strings.Trim(id, "/")

		if id == "" || strings.Contains(id, "/") ||
			strings.HasSuffix(id, "/status") {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "问题编号不正确",
			})
			return
		}

		store.mu.Lock()
		defer store.mu.Unlock()

		index := -1
		for i := range store.issues {
			if store.issues[i].ID == id {
				index = i
				break
			}
		}

		if index == -1 {
			writeJSON(w, http.StatusNotFound, map[string]string{
				"error": "没有找到对应的巡检问题",
			})
			return
		}

		// 暂存被删除的记录，以便保存失败时恢复。
		oldIssue := store.issues[index]
		oldIssues := append([]Issue(nil), store.issues...)

		store.issues = append(
			store.issues[:index],
			store.issues[index+1:]...,
		)

		if err := store.saveLocked(); err != nil {
			store.issues = oldIssues
			log.Printf("删除巡检问题失败：%v", err)

			writeJSON(w, http.StatusInternalServerError, map[string]string{
				"error": "问题删除失败，原记录已恢复",
			})
			return
		}

		writeJSON(w, http.StatusOK, map[string]any{
			"message": "巡检问题删除成功",
			"item":    oldIssue,
		})
	}
}

func updateIssueStatusHandler(store *IssueStore) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPatch {
			w.Header().Set("Allow", "PATCH")
			writeJSON(w, http.StatusMethodNotAllowed, map[string]string{
				"error": "此接口只支持 PATCH 方法",
			})
			return
		}

		// 从 /api/issues/{id}/status 中提取问题编号。
		path := strings.TrimPrefix(r.URL.Path, "/api/issues/")
		if !strings.HasSuffix(path, "/status") {
			http.NotFound(w, r)
			return
		}

		id := strings.TrimSuffix(path, "/status")
		id = strings.Trim(id, "/")
		if id == "" || strings.Contains(id, "/") {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "问题编号不正确",
			})
			return
		}

		defer r.Body.Close()

		var input struct {
			Status string `json:"status"`
		}

		r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		decoder := json.NewDecoder(r.Body)
		decoder.DisallowUnknownFields()

		if err := decoder.Decode(&input); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "请求 JSON 格式不正确",
			})
			return
		}

		// 兼容中文入参，但统一存英文枚举。
		input.Status = strings.TrimSpace(input.Status)
		switch input.Status {
		case "待处理":
			input.Status = StatusOpen
		case "处理中":
			input.Status = StatusInProgress
		case "已完成":
			input.Status = StatusResolved
		}

		if input.Status != StatusOpen &&
			input.Status != StatusInProgress &&
			input.Status != StatusResolved {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "status 只能是 open、in_progress 或 resolved",
			})
			return
		}

		store.mu.Lock()
		defer store.mu.Unlock()

		index := -1
		for i := range store.issues {
			if store.issues[i].ID == id {
				index = i
				break
			}
		}

		if index == -1 {
			writeJSON(w, http.StatusNotFound, map[string]string{
				"error": "没有找到对应的巡检问题",
			})
			return
		}

		oldStatus := store.issues[index].Status
		oldUpdatedAt := store.issues[index].UpdatedAt

		store.issues[index].Status = input.Status
		store.issues[index].UpdatedAt = time.Now().Format(time.RFC3339)

		if err := store.saveLocked(); err != nil {
			store.issues[index].Status = oldStatus
			store.issues[index].UpdatedAt = oldUpdatedAt
			log.Printf("保存问题状态失败：%v", err)
			writeJSON(w, http.StatusInternalServerError, map[string]string{
				"error": "问题状态保存失败",
			})
			return
		}

		writeJSON(w, http.StatusOK, map[string]any{
			"message": "问题状态更新成功",
			"item":    store.issues[index],
		})
	}
}

// =========================================================================
//  /api/inspections ——「最小可运行版」的扁平契约
// -------------------------------------------------------------------------
//  这一路与 /api/issues 的区别只在入参形状：
//    /api/issues      坐标是嵌套对象 position{x,y,z}
//    /api/inspections 坐标是顶层三个字段 x / y / z
//  两条路最终都落成同一条 Issue、存进同一个 store，
//  这样 Unity 提交完，React 管理端立刻能看到，不会出现「记录存到另一个文件里看不见」。
// =========================================================================

type InspectionRecord struct {
	TargetID    string  `json:"targetId"`
	TargetName  string  `json:"targetName"`
	Title       string  `json:"title"`
	Description string  `json:"description"`
	Priority    string  `json:"priority"`
	X           float64 `json:"x"`
	Y           float64 `json:"y"`
	Z           float64 `json:"z"`
	CreatedAt   string  `json:"createdAt"`
}

// rawRecordFile 是原始报文留档（append-only），只用于排查对照；
// 真正的唯一数据源仍然是 dataFile（issues.json）。
const rawRecordFile = "inspection_records.json"

var rawMu sync.Mutex

func appendRawRecord(record InspectionRecord) error {
	rawMu.Lock()
	defer rawMu.Unlock()

	var records []InspectionRecord

	data, err := os.ReadFile(rawRecordFile)
	if err == nil && len(data) > 0 {
		if err := json.Unmarshal(data, &records); err != nil {
			return err
		}
	} else if err != nil && !os.IsNotExist(err) {
		return err
	}

	records = append(records, record)

	output, err := json.MarshalIndent(records, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(rawRecordFile, output, 0644)
}

func validPriority(value string) bool {
	return value == PriorityLow || value == PriorityMedium || value == PriorityHigh
}

func inspectionsHandler(store *IssueStore) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.Header().Set("Allow", "POST")
			writeJSON(w, http.StatusMethodNotAllowed, map[string]string{
				"error": "此接口只支持 POST 方法",
			})
			return
		}

		r.Body = http.MaxBytesReader(w, r.Body, 1<<20) // 请求体上限 1MB，防止超大 payload
		defer r.Body.Close()

		// 这里刻意不开 DisallowUnknownFields()：
		// 一旦 Unity 端多传一个字段就会被打成 400，且错误信息只说「格式不正确」，排查成本极高。
		// 真正的业务校验放在下面的必填项与枚举判断上。
		var record InspectionRecord
		if err := json.NewDecoder(r.Body).Decode(&record); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "请求 JSON 格式不正确：" + err.Error(),
			})
			return
		}

		record.TargetID = strings.TrimSpace(record.TargetID)
		record.TargetName = strings.TrimSpace(record.TargetName)
		record.Title = strings.TrimSpace(record.Title)
		record.Description = strings.TrimSpace(record.Description)
		record.Priority = strings.TrimSpace(record.Priority)

		// 必填校验：设备标识 + 标题。Unity 端也拦了一道，这里是服务端兜底。
		if record.TargetID == "" || record.Title == "" {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "targetId 和 title 不能为空",
			})
			return
		}

		if record.Priority == "" {
			record.Priority = PriorityMedium
		}
		if !validPriority(record.Priority) {
			writeJSON(w, http.StatusBadRequest, map[string]string{
				"error": "priority 只能是 low、medium 或 high（收到：" + record.Priority + "）",
			})
			return
		}

		now := time.Now()

		createdAt := now.Format(time.RFC3339)
		if t := strings.TrimSpace(record.CreatedAt); t != "" {
			if parsed, err := time.Parse(time.RFC3339, t); err == nil {
				createdAt = parsed.Format(time.RFC3339)
			} else {
				log.Printf("客户端 createdAt 格式无法解析（%q），改用服务端时间", t)
			}
		}

		issue := Issue{
			ID:          now.Format("20060102150405.000000000"),
			TargetID:    record.TargetID,
			TargetName:  record.TargetName,
			Title:       record.Title,
			Description: record.Description,
			Priority:    record.Priority,
			Position:    Position{X: record.X, Y: record.Y, Z: record.Z},
			Status:      StatusOpen,
			CreatedAt:   createdAt,
			UpdatedAt:   createdAt,
		}

		if err := store.add(issue); err != nil {
			log.Printf("保存巡检记录失败：%v", err)
			writeJSON(w, http.StatusInternalServerError, map[string]string{
				"error": "巡检记录保存失败",
			})
			return
		}

		// 主流程已成功，留档失败只记日志不回滚，避免一次成功提交被判成失败。
		record.CreatedAt = createdAt
		if err := appendRawRecord(record); err != nil {
			log.Printf("写入 %s 失败（不影响本次提交）：%v", rawRecordFile, err)
		}

		writeJSON(w, http.StatusCreated, map[string]any{
			"success":  true,
			"message":  "巡检记录已保存",
			"targetId": issue.TargetID,
			"id":       issue.ID,
		})
	}
}

func main() {
	if env := strings.TrimSpace(os.Getenv("INSPECTION_DATA_FILE")); env != "" {
		dataFile = env
	}

	absolutePath, err := filepath.Abs(dataFile)
	if err != nil {
		log.Fatalf("获取数据文件路径失败：%v", err)
	}

	store, err := NewIssueStore(absolutePath)
	if err != nil {
		log.Fatalf("加载巡检问题数据失败：%v", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/health", healthHandler)

	mux.HandleFunc("/api/issues", issuesHandler(store))

	// Unity 端 InspectionApiClient 走这一路（扁平 x/y/z 入参）
	mux.HandleFunc("/api/inspections", inspectionsHandler(store))

	mux.HandleFunc("/api/issues/", func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodDelete {
			deleteIssueHandler(store)(w, r)
			return
		}

		updateIssueStatusHandler(store)(w, r)
	})

	addr := ":8080"

	log.Printf("巡检问题后端正在启动：http://localhost%s", addr)
	log.Printf("服务状态检查：http://localhost%s/api/health", addr)
	log.Printf("巡检问题列表：http://localhost%s/api/issues", addr)
	log.Printf("Unity 提交接口：POST http://localhost%s/api/inspections", addr)
	log.Printf("数据保存位置：%s", absolutePath)
	log.Printf("原始报文留档：%s", rawRecordFile)

	// 中间件顺序：先兜 panic，再挂 CORS（浏览器端的 React 才能跨域访问）。
	server := &http.Server{
		Addr:              addr,
		Handler:           withRecovery(withCORS(mux)),
		ReadHeaderTimeout: 10 * time.Second,
	}

	// 监听放到 goroutine 里，主线程留下来等退出信号。
	serverErrors := make(chan error, 1)
	go func() {
		serverErrors <- server.ListenAndServe()
	}()

	signalContext, stopListening := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stopListening()

	select {
	case err := <-serverErrors:
		// 主动 Shutdown 会以 ErrServerClosed 返回，那是正常退出，不算启动失败
		if errors.Is(err, http.ErrServerClosed) {
			return
		}
		log.Fatalf("后端服务启动失败：%v", err)
	case <-signalContext.Done():
	}

	log.Printf("收到退出信号，正在关闭服务（最多等待 5 秒）…")
	shutdownContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := server.Shutdown(shutdownContext); err != nil {
		log.Printf("关闭服务时出错：%v", err)
	}
	log.Printf("服务已停止")
}
