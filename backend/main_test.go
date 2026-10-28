package main

// 后端接口自动化测试：go test ./... 直接跑，不依赖任何外部服务、不碰真实数据。
//
// 两个关键设计（踩过的坑都写在这了）：
//   1. 每个用例都 chdir 到 t.TempDir()：
//      appendRawRecord 写的是**相对路径** inspection_records.json（原始报文留档），
//      不切目录就会把测试数据追加进真实的留档文件里。
//   2. store 用临时目录下的 issues.json：
//      「重启后数据还在」这类持久化断言，靠的是重新 NewIssueStore 读同一个文件，
//      而不是看内存里的切片 —— 内存里有不代表落盘了。

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func newTestServer(t *testing.T) (*http.ServeMux, *IssueStore, string, func()) {
	t.Helper()

	oldWd, err := os.Getwd()
	if err != nil {
		t.Fatalf("读取当前目录失败：%v", err)
	}

	dir := t.TempDir()
	if err := os.Chdir(dir); err != nil {
		t.Fatalf("切换到临时目录失败：%v", err)
	}

	dataFile := filepath.Join(dir, "issues.json")
	store, err := NewIssueStore(dataFile)
	if err != nil {
		t.Fatalf("创建测试用 store 失败：%v", err)
	}

	// 路由与 main() 完全一致：测试的是真实路由组合，不是单独某个 handler。
	mux := http.NewServeMux()
	mux.HandleFunc("/api/health", healthHandler)
	mux.HandleFunc("/api/issues", issuesHandler(store))
	mux.HandleFunc("/api/inspections", inspectionsHandler(store))
	mux.HandleFunc("/api/issues/", func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodDelete {
			deleteIssueHandler(store)(w, r)
			return
		}
		updateIssueStatusHandler(store)(w, r)
	})

	return mux, store, dataFile, func() { _ = os.Chdir(oldWd) }
}

func doRequest(t *testing.T, mux *http.ServeMux, method, path, body string) *httptest.ResponseRecorder {
	t.Helper()

	req := httptest.NewRequest(method, path, strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")

	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	return rec
}

func decodeBody(t *testing.T, rec *httptest.ResponseRecorder) map[string]any {
	t.Helper()

	var payload map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &payload); err != nil {
		t.Fatalf("响应不是合法 JSON（%d）：%s", rec.Code, rec.Body.String())
	}
	return payload
}

func listIssues(t *testing.T, mux *http.ServeMux) []Issue {
	t.Helper()

	rec := doRequest(t, mux, http.MethodGet, "/api/issues", "")
	if rec.Code != http.StatusOK {
		t.Fatalf("GET /api/issues 期望 200，实际 %d", rec.Code)
	}

	var payload struct {
		Items []Issue `json:"items"`
		Total int     `json:"total"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &payload); err != nil {
		t.Fatalf("列表响应解析失败：%v", err)
	}

	if payload.Total != len(payload.Items) {
		t.Fatalf("total(%d) 与 items 长度(%d) 不一致", payload.Total, len(payload.Items))
	}
	return payload.Items
}

func TestHealthEndpoint(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodGet, "/api/health", "")
	if rec.Code != http.StatusOK {
		t.Fatalf("/api/health 期望 200，实际 %d", rec.Code)
	}

	if body := decodeBody(t, rec); body["status"] != "ok" {
		t.Fatalf("health 响应里 status 应为 ok，实际 %v", body["status"])
	}
}

func TestCreateIssueRejectsEmptyTitle(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"title":"  ","priority":"high"}`)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("空标题期望 400，实际 %d：%s", rec.Code, rec.Body.String())
	}

	if items := listIssues(t, mux); len(items) != 0 {
		t.Fatalf("被拒绝的提交不该入库，实际有 %d 条", len(items))
	}
}

func TestCreateIssueRejectsUnknownPriority(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"title":"泵体异响","priority":"urgent"}`)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("非法优先级期望 400，实际 %d", rec.Code)
	}
}

func TestCreateIssueRejectsUnknownField(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	// DisallowUnknownFields：多一个后端不认识的字段就该 400，
	// 否则 Unity 端改了字段而后端没同步时，数据会被静默丢弃。
	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"title":"泵体异响","level":"high"}`)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("未知字段期望 400，实际 %d", rec.Code)
	}
}

func TestCreateIssueSavesZoneAndDefaultsWhenMissing(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	// 带区域
	rec := doRequest(t, mux, http.MethodPost, "/api/issues",
		`{"targetId":"PUMP-A","targetName":"水泵机组 A","zone":"一号泵房","title":"泵体异响","priority":"high","position":{"x":1.25,"y":0.8,"z":2.4}}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("合法提交期望 201，实际 %d：%s", rec.Code, rec.Body.String())
	}

	body := decodeBody(t, rec)
	item := body["item"].(map[string]any)
	if item["zone"] != "一号泵房" {
		t.Fatalf("区域应为「一号泵房」，实际 %v", item["zone"])
	}
	if item["status"] != StatusOpen {
		t.Fatalf("新建记录状态应为 open，实际 %v", item["status"])
	}
	if item["id"] == "" || item["id"] == nil {
		t.Fatalf("回包必须带 id（Unity 端靠它把标记和记录绑起来），实际 %v", item["id"])
	}

	// 不带区域 → normalize 兜底
	rec = doRequest(t, mux, http.MethodPost, "/api/issues", `{"targetId":"CABINET-A","title":"柜门变形"}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("不带区域也应能提交，实际 %d：%s", rec.Code, rec.Body.String())
	}

	item = decodeBody(t, rec)["item"].(map[string]any)
	if item["zone"] != DefaultZone {
		t.Fatalf("缺区域时应兜底为「%s」，实际 %v", DefaultZone, item["zone"])
	}
	if item["priority"] != PriorityMedium {
		t.Fatalf("缺优先级时应兜底为 medium，实际 %v", item["priority"])
	}
}

func TestIssuesPersistAcrossRestart(t *testing.T) {
	mux, _, dataFile, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues",
		`{"targetId":"VALVE-A","targetName":"主水管阀门 A","zone":"一号泵房","title":"阀门渗漏","priority":"low","position":{"x":0,"y":1.1,"z":-2}}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("提交失败：%d %s", rec.Code, rec.Body.String())
	}

	if items := listIssues(t, mux); len(items) != 1 {
		t.Fatalf("提交后应有 1 条，实际 %d 条", len(items))
	}

	// 重新用同一个文件建 store = 模拟服务重启，这是「数据真的落盘了」的唯一证据
	reloaded, err := NewIssueStore(dataFile)
	if err != nil {
		t.Fatalf("重新加载数据文件失败：%v", err)
	}

	items := reloaded.list()
	if len(items) != 1 {
		t.Fatalf("重启后应仍有 1 条，实际 %d 条", len(items))
	}
	if items[0].Title != "阀门渗漏" {
		t.Fatalf("重启后标题不对：%q", items[0].Title)
	}
	if items[0].Zone != "一号泵房" {
		t.Fatalf("重启后区域丢了：%q", items[0].Zone)
	}
}

func TestUpdateStatus(t *testing.T) {
	mux, _, dataFile, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"targetId":"PUMP-B","title":"轴承高温","priority":"high"}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("提交失败：%d", rec.Code)
	}

	id := decodeBody(t, rec)["item"].(map[string]any)["id"].(string)

	rec = doRequest(t, mux, http.MethodPatch, "/api/issues/"+id+"/status", `{"status":"in_progress"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("改状态期望 200，实际 %d：%s", rec.Code, rec.Body.String())
	}

	updated := decodeBody(t, rec)["item"].(map[string]any)
	if updated["status"] != StatusInProgress {
		t.Fatalf("回包状态应为 in_progress，实际 %v", updated["status"])
	}

	// 再拉一次列表，确认不是只改了内存
	items := listIssues(t, mux)
	if len(items) != 1 || items[0].Status != StatusInProgress {
		t.Fatalf("列表里状态没更新：%+v", items)
	}

	reloaded, err := NewIssueStore(dataFile)
	if err != nil {
		t.Fatalf("重新加载失败：%v", err)
	}
	if got := reloaded.list(); len(got) != 1 || got[0].Status != StatusInProgress {
		t.Fatalf("重启后状态没持久化：%+v", got)
	}
}

func TestUpdateStatusRejectsInvalidValue(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"targetId":"PUMP-C","title":"异响"}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("提交失败：%d", rec.Code)
	}
	id := decodeBody(t, rec)["item"].(map[string]any)["id"].(string)

	// 中文状态是后端**故意**兜底的（兼容早期中文数据）：接受并转成英文枚举，不是报错。
	// 这里测的是「转对了没」；三端统一发英文这条约束靠调用方保证，不靠后端 400。
	got := doRequest(t, mux, http.MethodPatch, "/api/issues/"+id+"/status", `{"status":"处理中"}`)
	if got.Code != http.StatusOK {
		t.Fatalf("中文状态应被兜底接受，期望 200，实际 %d：%s", got.Code, got.Body.String())
	}
	if status := decodeBody(t, got)["item"].(map[string]any)["status"]; status != StatusInProgress {
		t.Fatalf("中文「处理中」应转成 in_progress，实际 %v", status)
	}

	// 真正的非法值才该 400
	if bad := doRequest(t, mux, http.MethodPatch, "/api/issues/"+id+"/status", `{"status":"urgent"}`); bad.Code != http.StatusBadRequest {
		t.Fatalf("非法状态期望 400，实际 %d", bad.Code)
	}

	// 接口只接受 status 一个字段（DisallowUnknownFields）
	if extra := doRequest(t, mux, http.MethodPatch, "/api/issues/"+id+"/status", `{"status":"open","priority":"high"}`); extra.Code != http.StatusBadRequest {
		t.Fatalf("多余字段期望 400，实际 %d", extra.Code)
	}
}

func TestUpdateStatusNotFound(t *testing.T) {
	mux, _, _, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPatch, "/api/issues/not-exist-id/status", `{"status":"resolved"}`)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("不存在的编号期望 404，实际 %d", rec.Code)
	}
}

func TestDeleteIssue(t *testing.T) {
	mux, _, dataFile, cleanup := newTestServer(t)
	defer cleanup()

	rec := doRequest(t, mux, http.MethodPost, "/api/issues", `{"targetId":"CABINET-B","title":"指示灯坏"}`)
	if rec.Code != http.StatusCreated {
		t.Fatalf("提交失败：%d", rec.Code)
	}
	id := decodeBody(t, rec)["item"].(map[string]any)["id"].(string)

	if got := doRequest(t, mux, http.MethodDelete, "/api/issues/"+id, ""); got.Code != http.StatusOK {
		t.Fatalf("删除期望 200，实际 %d：%s", got.Code, got.Body.String())
	}

	if items := listIssues(t, mux); len(items) != 0 {
		t.Fatalf("删除后应为空，实际 %d 条", len(items))
	}

	// 删两次：第二次必须 404，不能因为幂等就返回 200 骗调用方
	if got := doRequest(t, mux, http.MethodDelete, "/api/issues/"+id, ""); got.Code != http.StatusNotFound {
		t.Fatalf("重复删除期望 404，实际 %d", got.Code)
	}

	reloaded, err := NewIssueStore(dataFile)
	if err != nil {
		t.Fatalf("重新加载失败：%v", err)
	}
	if got := reloaded.list(); len(got) != 0 {
		t.Fatalf("重启后被删的记录又回来了：%+v", got)
	}
}

func TestLegacyRecordsAreNormalized(t *testing.T) {
	// 这个用例只验证「读文件时的归一化」，不发请求，所以 mux 用不到
	_, _, dataFile, cleanup := newTestServer(t)
	defer cleanup()

	// 早期数据：没有 zone，状态是中文，优先级也是中文
	legacy := `[{"id":"legacy-1","targetId":"PUMP-A","targetName":"水泵机组 A","title":"旧记录","priority":"紧急","status":"处理中","createdAt":"2026-10-20T10:00:00+08:00"}]`
	if err := os.WriteFile(dataFile, []byte(legacy), 0644); err != nil {
		t.Fatalf("写入历史数据失败：%v", err)
	}

	reloaded, err := NewIssueStore(dataFile)
	if err != nil {
		t.Fatalf("加载历史数据失败：%v", err)
	}

	items := reloaded.list()
	if len(items) != 1 {
		t.Fatalf("应有 1 条历史记录，实际 %d", len(items))
	}

	if items[0].Priority != PriorityHigh {
		t.Fatalf("中文优先级应归一成 high，实际 %q", items[0].Priority)
	}
	if items[0].Status != StatusInProgress {
		t.Fatalf("中文状态应归一成 in_progress，实际 %q", items[0].Status)
	}
	if items[0].Zone != DefaultZone {
		t.Fatalf("缺区域的历史记录应归到「%s」，实际 %q", DefaultZone, items[0].Zone)
	}
	if items[0].UpdatedAt == "" {
		t.Fatal("缺 updatedAt 时应回落到 createdAt")
	}

	// 注意：这里**不**用 mux 发请求验证 —— 服务启动时就把数据读进内存了，
	// 直接改文件不会影响已经建好的 store。想让接口读到旧数据必须重启服务，
	// 而「重启后归一化生效」这件事由 NewIssueStore 这条路径证明就够了。
}

// TestRecoveryMiddlewareTurnsPanicInto500：handler 内部 panic 时必须返回 500，
// 而不是让整个进程崩掉 —— 否则一次意外会把正在处理的其他请求一起带走。
func TestRecoveryMiddlewareTurnsPanicInto500(t *testing.T) {
	panicking := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		panic("模拟 handler 内部崩溃")
	})

	recorder := httptest.NewRecorder()
	withRecovery(panicking).ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/api/issues", nil))

	if recorder.Code != http.StatusInternalServerError {
		t.Fatalf("panic 时应返回 500，实际 %d", recorder.Code)
	}
	if !strings.Contains(recorder.Body.String(), "服务器内部错误") {
		t.Fatalf("响应体应给出错误说明，实际 %q", recorder.Body.String())
	}
}
