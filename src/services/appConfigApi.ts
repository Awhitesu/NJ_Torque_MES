import type { AppConfig } from '../types/mes'
import { withApiBase } from './apiBase'

interface SaveConfigResponse {
  config: AppConfig
  filePath: string
}

const getBaseUrl = () => withApiBase('/app-config')

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `HTTP ${response.status}`)
  }
  return response.json() as Promise<T>
}

export function getAppConfig() {
  return requestJson<AppConfig>(getBaseUrl())
}

export function saveAppConfig(config: AppConfig) {
  return requestJson<SaveConfigResponse>(getBaseUrl(), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(config)
  })
}

export function pickConfigDirectory(title: string) {
  return requestJson<{ path: string }>(`${getBaseUrl()}/pick-directory`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title })
  })
}
