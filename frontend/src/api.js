// Thin client for the CQRS API. Reads go through `query`-style GETs, writes through commands (POST/PUT/DELETE).
// The session is an HttpOnly cookie set by /api/auth/login, so requests just need same-origin credentials.

export class ApiError extends Error {
  constructor(message, status, fieldErrors = {}, details = []) {
    super(message)
    this.status = status
    this.fieldErrors = fieldErrors
    this.details = details // every message, e.g. one per bad row in an Excel import
  }
}

// Called when any request comes back 401 (session expired or signed out elsewhere).
let onUnauthorized = () => {}
export const setUnauthorizedHandler = (fn) => (onUnauthorized = fn)

async function toError(res) {
  const data = await res.json().catch(() => null)
  // ASP.NET ProblemDetails: { title, detail, errors: { field: [msg] } }
  const lists = Object.entries(data?.errors ?? {}).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), v])
  const errors = Object.fromEntries(lists.map(([k, v]) => [k, v.join(' ')]))
  const details = lists.flatMap(([, v]) => v)
  const message = data?.detail || details[0] || data?.title || `Request failed (${res.status})`
  return new ApiError(message, res.status, errors, details)
}

async function send(method, url, { json, form } = {}) {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: json ? { 'Content-Type': 'application/json' } : undefined,
    body: json ? JSON.stringify(json) : form,
  })
  if (res.status === 401 && !url.startsWith('/api/auth/')) onUnauthorized()
  if (!res.ok) throw await toError(res)
  return res
}

async function request(method, url, body) {
  const res = await send(method, url, { json: body })
  if (res.status === 204) return null
  return res.json().catch(() => null)
}

/** Fetches a file and hands it to the browser as a download. */
async function download(url, fallbackName) {
  const res = await send('GET', url)
  const name = /filename="?([^";]+)"?/.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName
  const href = URL.createObjectURL(await res.blob())
  const a = Object.assign(document.createElement('a'), { href, download: name })
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(href), 1000)
}

const qs = (params) => {
  const p = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== ''))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const api = {
  // Session
  me: () => request('GET', '/api/auth/me'),
  login: (email, password) => request('POST', '/api/auth/login', { email, password }),
  authConfig: () => request('GET', '/api/auth/config'),
  googleSignIn: (credential) => request('POST', '/api/auth/google', { credential }),
  logout: () => request('POST', '/api/auth/logout'),

  // Queries
  dashboard: () => request('GET', '/api/dashboard'),
  borrowers: (search) => request('GET', `/api/borrowers${qs({ search })}`),
  borrower: (id) => request('GET', `/api/borrowers/${id}`),
  loans: (filters = {}) => request('GET', `/api/loans${qs(filters)}`),
  loan: (id) => request('GET', `/api/loans/${id}`),
  history: (filters = {}) => request('GET', `/api/history${qs(filters)}`),

  // Excel
  exportLoans: (filters = {}) => download(`/api/loans/export${qs(filters)}`, 'loans.xlsx'),
  downloadImportTemplate: () => download('/api/loans/import-template', 'loan-import-template.xlsx'),
  importLoans: (file) => {
    const form = new FormData()
    form.append('file', file)
    return send('POST', '/api/loans/import', { form }).then((r) => r.json())
  },
  exportLoan: (id) => download(`/api/loans/${id}/export`, 'loan.xlsx'),
  exportBorrower: (id) => download(`/api/borrowers/${id}/export`, 'borrower.xlsx'),
  importBorrower: (id, file) => {
    const form = new FormData()
    form.append('file', file)
    return send('POST', `/api/borrowers/${id}/import`, { form }).then((r) => r.json())
  },

  // Commands
  createBorrower: (input) => request('POST', '/api/borrowers', input),
  updateBorrower: (id, input) => request('PUT', `/api/borrowers/${id}`, input),
  deleteBorrower: (id) => request('DELETE', `/api/borrowers/${id}`),

  createLoan: (input) => request('POST', '/api/loans', input),
  updateLoan: (id, input) => request('PUT', `/api/loans/${id}`, input),
  deleteLoan: (id) => request('DELETE', `/api/loans/${id}`),

  recordPayment: (loanId, input) => request('POST', `/api/loans/${loanId}/payments`, input),
  updatePayment: (id, input) => request('PUT', `/api/payments/${id}`, input),
  deletePayment: (id) => request('DELETE', `/api/payments/${id}`),
}

export const LOAN_STATUSES = ['Pending', 'Active', 'PaidOff', 'Defaulted', 'Cancelled']
