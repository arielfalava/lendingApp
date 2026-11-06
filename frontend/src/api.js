// Thin client for the CQRS API. Reads go through `query`-style GETs, writes through commands (POST/PUT/DELETE).

export class ApiError extends Error {
  constructor(message, status, fieldErrors = {}) {
    super(message)
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

async function request(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return null
  const data = await res.json().catch(() => null)
  if (!res.ok) {
    // ASP.NET ProblemDetails: { title, detail, errors: { field: [msg] } }
    const errors = Object.fromEntries(
      Object.entries(data?.errors ?? {}).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), v.join(' ')]),
    )
    throw new ApiError(data?.detail || Object.values(errors)[0] || data?.title || `Request failed (${res.status})`, res.status, errors)
  }
  return data
}

const qs = (params) => {
  const p = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== ''))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const api = {
  // Queries
  dashboard: () => request('GET', '/api/dashboard'),
  borrowers: (search) => request('GET', `/api/borrowers${qs({ search })}`),
  borrower: (id) => request('GET', `/api/borrowers/${id}`),
  loans: (filters = {}) => request('GET', `/api/loans${qs(filters)}`),
  loan: (id) => request('GET', `/api/loans/${id}`),
  history: (filters = {}) => request('GET', `/api/history${qs(filters)}`),

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
