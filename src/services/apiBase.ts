const API_BASE_STORAGE_KEY = 'NJ_TORQUE_API_BASE_URL'
const DEFAULT_API_BASE_URL = '/api'

function normalizeApiBase(raw?: string): string {
  const v = String(raw || '').trim()
  if (!v) return DEFAULT_API_BASE_URL
  return v.endsWith('/') ? v.slice(0, -1) : v
}

export function getRuntimeApiBaseUrl(): string {
  if (typeof window === 'undefined') return DEFAULT_API_BASE_URL
  return normalizeApiBase(window.localStorage.getItem(API_BASE_STORAGE_KEY) || DEFAULT_API_BASE_URL)
}

export function setRuntimeApiBaseUrl(raw?: string): void {
  if (typeof window === 'undefined') return
  window.localStorage.setItem(API_BASE_STORAGE_KEY, normalizeApiBase(raw))
}

export function withApiBase(path: string): string {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return `${getRuntimeApiBaseUrl()}${normalizedPath}`
}

export function buildFromApiBase(apiBaseUrl: string | undefined, path: string): string {
  const base = normalizeApiBase(apiBaseUrl)
  if (!path) return base
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return `${base}${normalizedPath}`
}
