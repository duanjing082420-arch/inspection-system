import { useCallback, useEffect, useState } from 'react'
import './App.css'

// 字段与 Go 后端 Issue 结构严格对齐：
//   id / targetId / targetName / title / description / priority / position / status / createdAt / updatedAt
// 后端存的是英文枚举（priority: low|medium|high，status: open|in_progress|resolved），
// 界面显示中文，但发回去的一定是英文 —— 否则后端会返回 400「status 只能是 open、in_progress 或 resolved」。
type Position = { x: number; y: number; z: number }

type Issue = {
  id: string
  targetId: string
  targetName: string
  zone: string
  title: string
  description: string
  priority: string
  position: Position | null
  status: string
  createdAt: string
  updatedAt: string
}

const STATUS_VALUES = ['open', 'in_progress', 'resolved'] as const
const STATUS_LABEL: Record<string, string> = {
  open: '待处理',
  in_progress: '处理中',
  resolved: '已解决',
}

const PRIORITY_VALUES = ['high', 'medium', 'low'] as const
const PRIORITY_LABEL: Record<string, string> = {
  high: '高',
  medium: '中',
  low: '低',
}

// 侧边栏的四个工作区。原先这几项只写了样式、没有点击处理，
// 所以「巡检问题 / 处理记录 / 系统设置」点了没有任何反应。
// 改成数组驱动 + 一个 view 状态，导航和内容区都跟着它走。
type ViewKey = 'overview' | 'issues' | 'records' | 'settings'

const NAV_ITEMS: { key: ViewKey; icon: string; label: string }[] = [
  { key: 'overview', icon: '▦', label: '问题总览' },
  { key: 'issues', icon: '▤', label: '巡检问题' },
  { key: 'records', icon: '◷', label: '处理记录' },
  { key: 'settings', icon: '⚙', label: '系统设置' },
]

const VIEW_META: Record<
  ViewKey,
  { breadcrumb: string; eyebrow: string; title: string; desc: string }
> = {
  overview: {
    breadcrumb: '问题总览',
    eyebrow: 'INSPECTION OVERVIEW',
    title: '巡检问题总览',
    desc: '统一记录、跟踪和处理现场巡检中发现的问题。',
  },
  issues: {
    breadcrumb: '巡检问题',
    eyebrow: 'ISSUE REGISTRY',
    title: '巡检问题',
    desc: '全部已登记的问题，可按关键词、优先级和区域筛选。',
  },
  records: {
    breadcrumb: '处理记录',
    eyebrow: 'HANDLING LOG',
    title: '处理记录',
    desc: '已经进入处理流程的问题；「待处理」的不在此列。',
  },
  settings: {
    breadcrumb: '系统设置',
    eyebrow: 'SYSTEM SETTINGS',
    title: '系统设置',
    desc: '服务连接状态与前后端的接口约定。',
  },
}

const emptyForm = {
  title: '',
  description: '',
  priority: 'medium',
  targetName: '',
}

function formatPosition(position: Position | null) {
  if (!position) return '—'
  const { x, y, z } = position
  return `(${x.toFixed(2)}, ${y.toFixed(2)}, ${z.toFixed(2)})`
}

function formatTime(value: string) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString('zh-CN', { hour12: false })
}

function App() {
  const [issues, setIssues] = useState<Issue[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState(emptyForm)
  const [saving, setSaving] = useState(false)
  const [filter, setFilter] = useState('all')
  const [keyword, setKeyword] = useState('')
  const [priorityFilter, setPriorityFilter] = useState('all')
  const [zoneFilter, setZoneFilter] = useState('all')
  const [selectedIssue, setSelectedIssue] = useState<Issue | null>(null)
  const [view, setView] = useState<ViewKey>('overview')
  // 系统设置页的服务状态：只有点开设置才需要，但启动时先探一次，导航上也能看出通不通
  const [health, setHealth] = useState<'idle' | 'checking' | 'ok' | 'fail'>('idle')
  const [healthText, setHealthText] = useState('')

  const loadIssues = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const response = await fetch('/api/issues')
      if (!response.ok) throw new Error(`请求失败：HTTP ${response.status}`)
      const data = await response.json()
      setIssues(data.items ?? [])
    } catch {
      // 后端不可用时必须给出提示，不能直接白屏
      setError('无法连接后端。请确认 Go 服务正在 localhost:8080 运行。')
    } finally {
      setLoading(false)
    }
  }, [])

  // 探一次 /api/health，给「系统设置」页和右上角的状态点提供依据
  const checkHealth = useCallback(async () => {
    setHealth('checking')
    try {
      const response = await fetch('/api/health')
      if (!response.ok) throw new Error(String(response.status))
      const data = await response.json()
      setHealth('ok')
      setHealthText(data.timestamp ? `最近一次检测：${formatTime(data.timestamp)}` : '服务正常')
    } catch {
      setHealth('fail')
      setHealthText('无法连接后端，请确认 Go 服务运行在 localhost:8080')
    }
  }, [])

  useEffect(() => {
    void loadIssues()
    void checkHealth()
  }, [loadIssues, checkHealth])

  async function createIssue(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSaving(true)
    setNotice('')
    setError('')

    try {
      const response = await fetch('/api/issues', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        // 后端开了 DisallowUnknownFields，字段名必须完全一致，多一个字段就是 400
        body: JSON.stringify({
          title: form.title,
          description: form.description,
          priority: form.priority,
          targetName: form.targetName,
          position: { x: 0, y: 0, z: 0 },
        }),
      })
      const data = await response.json()
      if (!response.ok) throw new Error(data.error || '新增失败')

      setForm(emptyForm)
      setShowForm(false)
      setNotice('巡检问题新增成功')
      await loadIssues()
    } catch (err) {
      setError(err instanceof Error ? err.message : '新增失败')
    } finally {
      setSaving(false)
    }
  }

  async function updateStatus(issue: Issue, status: string) {
    setNotice('')
    setError('')

    try {
      const response = await fetch(
        `/api/issues/${encodeURIComponent(issue.id)}/status`,
        {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          // 发英文枚举，不能发中文
          body: JSON.stringify({ status }),
        },
      )
      const data = await response.json()
      if (!response.ok) throw new Error(data.error || '状态更新失败')

      setNotice(`“${issue.title}”已更新为${STATUS_LABEL[status] ?? status}`)
      await loadIssues()
    } catch (err) {
      setError(err instanceof Error ? err.message : '状态更新失败')
    }
  }

  async function deleteIssue(issue: Issue) {
    const confirmed = window.confirm(
      `确定要删除这条巡检问题吗？\n\n问题标题：${issue.title}\n问题编号：${issue.id}\n\n删除后无法恢复。`,
    )
    if (!confirmed) return

    setNotice('')
    setError('')

    try {
      const response = await fetch(
        `/api/issues/${encodeURIComponent(issue.id)}`,
        { method: 'DELETE' },
      )
      const data = await response.json()
      if (!response.ok) throw new Error(data.error || '删除失败')

      setSelectedIssue((current) => (current?.id === issue.id ? null : current))
      setNotice(`“${issue.title}”已删除`)
      await loadIssues()
    } catch (err) {
      setError(err instanceof Error ? err.message : '删除失败，请稍后重试')
    }
  }

  const pending = issues.filter((item) => item.status === 'open').length
  const processing = issues.filter((item) => item.status === 'in_progress').length
  const completed = issues.filter((item) => item.status === 'resolved').length

  // 区域下拉按现有数据去重生成：后端新增了 zone 字段，但老记录可能还没带，
  // 统一按「未分区」显示，避免筛选出现空选项。
  const zoneOptions = Array.from(
    new Set(issues.map((item) => (item.zone ? item.zone : '未分区'))),
  ).sort((a, b) => a.localeCompare(b, 'zh-CN'))

  const visibleIssues = issues.filter((item) => {
    const matchesStatus = filter === 'all' || item.status === filter
    const matchesPriority =
      priorityFilter === 'all' || item.priority === priorityFilter

    const itemZone = item.zone ? item.zone : '未分区'
    const matchesZone = zoneFilter === 'all' || itemZone === zoneFilter

    const searchText = [item.title, item.description, item.targetName, item.targetId, itemZone]
      .join(' ')
      .toLowerCase()

    return (
      matchesStatus &&
      matchesPriority &&
      matchesZone &&
      searchText.includes(keyword.trim().toLowerCase())
    )
  })

  // 「处理记录」只留已经动过的问题（处理中 / 已解决）。
  // 如果把待处理的也列进去，这一页就和「巡检问题」没区别了。
  const displayedIssues =
    view === 'records'
      ? visibleIssues.filter((item) => item.status !== 'open')
      : visibleIssues

  // 侧边栏「处理记录」上的角标数：已经动过的问题有几条
  const recordsCount = issues.filter((item) => item.status !== 'open').length

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-icon">巡</div>
          <div>
            <strong>巡检管理平台</strong>
            <span>INSPECTION SYSTEM</span>
          </div>
        </div>

        <div className="nav-label">工作台</div>
        {NAV_ITEMS.filter((item) => item.key !== 'settings').map((item) => (
          <button
            key={item.key}
            type="button"
            className={`nav-item ${view === item.key ? 'active' : ''}`}
            onClick={() => setView(item.key)}
            aria-current={view === item.key ? 'page' : undefined}
          >
            <span>{item.icon}</span> {item.label}
            {item.key === 'records' && recordsCount > 0 && (
              <span className="nav-badge">{recordsCount}</span>
            )}
          </button>
        ))}

        <div className="nav-label nav-label-lower">系统</div>
        <button
          type="button"
          className={`nav-item ${view === 'settings' ? 'active' : ''}`}
          onClick={() => setView('settings')}
          aria-current={view === 'settings' ? 'page' : undefined}
        >
          <span>⚙</span> 系统设置
        </button>

        <div className="sidebar-bottom">
          <div className="avatar">巡</div>
          <div className="user-info">
            <strong>巡检管理员</strong>
            <span>系统管理人员</span>
          </div>
          <span className="online-dot" />
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="breadcrumb">工作台 <span>/</span> {VIEW_META[view].breadcrumb}</div>
          <div className="topbar-right">
            <span className="service-dot" />
            <span>本地服务</span>
            <div className="top-avatar">管</div>
          </div>
        </header>

        <div className="content">
          <section className="welcome">
            <div>
              <div className="eyebrow">{VIEW_META[view].eyebrow}</div>
              <h1>{VIEW_META[view].title}</h1>
              <p>{VIEW_META[view].desc}</p>
            </div>
            {view !== 'settings' && view !== 'records' && (
              <button className="primary-button" onClick={() => {
                setShowForm(!showForm)
                setNotice('')
                setError('')
              }}>
                <span>＋</span> 新增巡检问题
              </button>
            )}
          </section>

          {notice && <div className="notice success-notice">✓ {notice}</div>}
          {error && <div className="notice error-notice">! {error}</div>}

          {showForm && (
            <section className="panel form-panel">
              <div className="panel-heading">
                <div>
                  <h2>登记巡检问题</h2>
                  <p>请填写现场发现的问题信息</p>
                </div>
                <button className="icon-button" onClick={() => setShowForm(false)}>×</button>
              </div>

              <form onSubmit={createIssue} className="issue-form">
                <label>
                  问题标题 <span className="required">*</span>
                  <input
                    required
                    maxLength={100}
                    placeholder="例如：水泵异响"
                    value={form.title}
                    onChange={(e) => setForm({ ...form, title: e.target.value })}
                  />
                </label>

                <label>
                  设备名称
                  <input
                    maxLength={100}
                    placeholder="例如：水泵机组 A（可留空）"
                    value={form.targetName}
                    onChange={(e) => setForm({ ...form, targetName: e.target.value })}
                  />
                </label>

                <label>
                  优先级
                  <select
                    value={form.priority}
                    onChange={(e) => setForm({ ...form, priority: e.target.value })}
                  >
                    {PRIORITY_VALUES.map((value) => (
                      <option key={value} value={value}>{PRIORITY_LABEL[value]}</option>
                    ))}
                  </select>
                </label>

                <label>
                  问题描述
                  <textarea
                    rows={3}
                    maxLength={1000}
                    placeholder="描述现场情况、潜在风险或整改要求"
                    value={form.description}
                    onChange={(e) => setForm({ ...form, description: e.target.value })}
                  />
                </label>

                <div className="form-actions">
                  <button type="button" className="secondary-button" onClick={() => setShowForm(false)}>
                    取消
                  </button>
                  <button type="submit" className="primary-button" disabled={saving}>
                    {saving ? '正在保存…' : '提交问题'}
                  </button>
                </div>
              </form>
            </section>
          )}

          {view === 'overview' && (
          <section className="stats-grid">
            <div className="stat-card">
              <div className="stat-top"><span>问题总数</span><div className="stat-icon purple">▤</div></div>
              <div className="stat-number">{issues.length}<small> 条</small></div>
              <div className="stat-foot">系统累计登记的问题</div>
            </div>
            <div className="stat-card">
              <div className="stat-top"><span>待处理</span><div className="stat-icon orange">◷</div></div>
              <div className="stat-number">{pending}<small> 条</small></div>
              <div className="stat-foot">等待开始整改</div>
            </div>
            <div className="stat-card">
              <div className="stat-top"><span>处理中</span><div className="stat-icon blue">↻</div></div>
              <div className="stat-number">{processing}<small> 条</small></div>
              <div className="stat-foot">正在跟进处理</div>
            </div>
            <div className="stat-card">
              <div className="stat-top"><span>已解决</span><div className="stat-icon green">✓</div></div>
              <div className="stat-number">{completed}<small> 条</small></div>
              <div className="stat-foot">已完成整改的问题</div>
            </div>
          </section>
          )}

          {view !== 'settings' && (
          <section className="panel table-panel">
            <div className="panel-heading table-heading">
              <div>
                <h2>{view === 'records' ? '处理记录明细' : '巡检问题记录'}</h2>
                <p>
                  {view === 'records'
                    ? '只列出已经进入处理流程的问题'
                    : '查看问题详情，并跟踪整改进度'}
                </p>
              </div>
              <button className="secondary-button refresh-button" onClick={() => void loadIssues()}>
                ↻ 刷新列表
              </button>
            </div>

            <div className="search-row">
              <input
                className="search-input"
                type="search"
                placeholder="搜索问题标题、描述或设备名称..."
                value={keyword}
                onChange={(e) => setKeyword(e.target.value)}
              />

              <select
                className="level-filter"
                value={priorityFilter}
                onChange={(e) => setPriorityFilter(e.target.value)}
                aria-label="按优先级筛选"
              >
                <option value="all">全部优先级</option>
                {PRIORITY_VALUES.map((value) => (
                  <option key={value} value={value}>{PRIORITY_LABEL[value]}优先级</option>
                ))}
              </select>

              <select
                className="level-filter"
                value={zoneFilter}
                onChange={(e) => setZoneFilter(e.target.value)}
                aria-label="按区域筛选"
              >
                <option value="all">全部区域</option>
                {zoneOptions.map((zone) => (
                  <option key={zone} value={zone}>{zone}</option>
                ))}
              </select>

              <button
                className="secondary-button"
                onClick={() => {
                  setKeyword('')
                  setPriorityFilter('all')
                  setZoneFilter('all')
                  setFilter('all')
                }}
              >
                重置筛选
              </button>
            </div>

            <div className="filter-row">
              {[{ value: 'all', label: '全部' }].concat(
                STATUS_VALUES.map((value) => ({ value, label: STATUS_LABEL[value] })),
              ).map((item) => (
                <button
                  key={item.value}
                  className={`filter-button ${filter === item.value ? 'selected' : ''}`}
                  onClick={() => setFilter(item.value)}
                >
                  {item.label}
                  {item.value === 'all' && <span>{issues.length}</span>}
                </button>
              ))}
            </div>

            {loading ? (
              <div className="empty-state">正在加载巡检问题…</div>
            ) : displayedIssues.length === 0 ? (
              <div className="empty-state">
                <div className="empty-icon">{view === 'records' ? '◷' : '▤'}</div>
                <strong>{view === 'records' ? '还没有处理过的记录' : '暂无巡检问题'}</strong>
                <p>
                  {view === 'records'
                    ? '把某条问题的状态改成「处理中」或「已解决」，它就会出现在这里。'
                    : filter === 'all'
                      ? '点击右上角“新增巡检问题”开始登记。'
                      : '当前筛选条件下没有问题记录。'}
                </p>
              </div>
            ) : (
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>问题信息</th>
                      <th>设备</th>
                      <th>优先级</th>
                      <th>创建时间</th>
                      <th>处理状态</th>
                      <th>操作</th>
                    </tr>
                  </thead>
                  <tbody>
                    {displayedIssues.map((issue) => (
                      <tr key={issue.id}>
                        <td className="issue-cell">
                          <button
                            type="button"
                            className="issue-title-button"
                            onClick={() => setSelectedIssue(issue)}
                          >
                            {issue.title}
                          </button>
                          <span>{issue.description || '暂无描述'}</span>
                        </td>
                        <td>
                          <div>{issue.targetName || '未知设备'}</div>
                          <span className="zone-hint">{issue.zone || '未分区'}</span>
                        </td>
                        <td>
                          <span className={`level-tag ${issue.priority === 'high' ? 'level-high' : issue.priority === 'medium' ? 'level-medium' : 'level-low'}`}>
                            {PRIORITY_LABEL[issue.priority] ?? issue.priority ?? '—'}
                          </span>
                        </td>
                        <td className="date-cell">{formatTime(issue.createdAt)}</td>
                        <td>
                          <span className={`status-tag ${issue.status === 'resolved' ? 'status-done' : issue.status === 'in_progress' ? 'status-progress' : 'status-pending'}`}>
                            <span />{STATUS_LABEL[issue.status] ?? issue.status}
                          </span>
                        </td>

                        <td>
                          <div className="issue-actions">
                            <select
                              className="status-select"
                              aria-label={`更新${issue.title}的状态`}
                              value={STATUS_VALUES.includes(issue.status as typeof STATUS_VALUES[number]) ? issue.status : 'open'}
                              onChange={(e) => void updateStatus(issue, e.target.value)}
                            >
                              {STATUS_VALUES.map((value) => (
                                <option key={value} value={value}>{STATUS_LABEL[value]}</option>
                              ))}
                            </select>

                            <button
                              type="button"
                              className="delete-button"
                              onClick={() => void deleteIssue(issue)}
                              aria-label={`删除${issue.title}`}
                              title="删除这条巡检问题"
                            >
                              删除
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <div className="table-footer">共 {displayedIssues.length} 条记录 <span>数据来自本地 Go 服务</span></div>
          </section>
          )}

          {view === 'settings' && (
            <section className="panel settings-panel">
              <div className="panel-heading table-heading">
                <div>
                  <h2>服务连接</h2>
                  <p>前端通过 vite 代理访问 Go 后端，开发环境不需要配跨域</p>
                </div>
                <button
                  className="secondary-button refresh-button"
                  onClick={() => void checkHealth()}
                  disabled={health === 'checking'}
                >
                  ↻ 重新检测
                </button>
              </div>

              <div className="settings-grid">
                <div className="settings-item">
                  <span>接口前缀</span>
                  <strong>/api</strong>
                  <small>由 vite.config.ts 代理到 localhost:8080</small>
                </div>
                <div className="settings-item">
                  <span>服务状态</span>
                  <strong
                    className={
                      health === 'ok' ? 'state-ok' : health === 'fail' ? 'state-fail' : ''
                    }
                  >
                    {health === 'ok'
                      ? '正常'
                      : health === 'checking'
                        ? '检测中…'
                        : health === 'fail'
                          ? '无法连接'
                          : '未检测'}
                  </strong>
                  <small>{healthText || '点右上角重新检测'}</small>
                </div>
                <div className="settings-item">
                  <span>数据条数</span>
                  <strong>{issues.length} 条</strong>
                  <small>
                    待处理 {pending} · 处理中 {processing} · 已解决 {completed}
                  </small>
                </div>
              </div>

              <div className="settings-list">
                <h3>接口约定</h3>
                <ul>
                  <li><code>GET /api/health</code><span>服务健康检查</span></li>
                  <li><code>GET /api/issues</code><span>拉取全部问题</span></li>
                  <li><code>POST /api/issues</code><span>新增问题（新记录状态固定为 open）</span></li>
                  <li><code>PATCH /api/issues/:id/status</code><span>改处理状态，只接受英文枚举</span></li>
                  <li><code>DELETE /api/issues/:id</code><span>删除问题</span></li>
                </ul>
                <p className="settings-note">
                  状态值必须传 <code>open</code> / <code>in_progress</code> / <code>resolved</code>，
                  传中文会被后端拒绝（400）。界面显示中文，传输用英文。
                </p>
              </div>
            </section>
          )}

          <footer className="page-footer">
            <span>现场巡检问题管理系统</span>
            <span>React · TypeScript · Go API</span>
          </footer>
        </div>
      </main>

      {selectedIssue && (
        <div className="modal-backdrop" onClick={() => setSelectedIssue(null)}>
          <section
            className="detail-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="detail-title"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="panel-heading">
              <h2 id="detail-title">巡检问题详情</h2>
              <button
                className="icon-button"
                onClick={() => setSelectedIssue(null)}
                aria-label="关闭详情"
              >
                ×
              </button>
            </div>

            <div className="detail-content">
              <h3>{selectedIssue.title}</h3>
              <p><strong>设备名称：</strong>{selectedIssue.targetName || '未知设备'}</p>
              <p><strong>设备编号：</strong>{selectedIssue.targetId || '—'}</p>
              <p><strong>所属区域：</strong>{selectedIssue.zone || '未分区'}</p>
              <p><strong>优先级：</strong>{PRIORITY_LABEL[selectedIssue.priority] ?? '—'}</p>
              <p><strong>处理状态：</strong>{STATUS_LABEL[selectedIssue.status] ?? selectedIssue.status}</p>
              <p><strong>场景坐标：</strong>{formatPosition(selectedIssue.position)}</p>
              <p><strong>记录编号：</strong>{selectedIssue.id}</p>
              <p><strong>创建时间：</strong>{formatTime(selectedIssue.createdAt)}</p>
              <p><strong>更新时间：</strong>{formatTime(selectedIssue.updatedAt)}</p>
              <p><strong>问题描述：</strong></p>
              <div className="detail-description">
                {selectedIssue.description || '暂无问题描述'}
              </div>
            </div>
          </section>
        </div>
      )}
    </div>
  )
}

export default App
