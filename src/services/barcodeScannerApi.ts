export interface BarcodeScannerStartRequest {
  scannerIp: string
  scannerPort: number
  barcodeRegex: string
}

export interface BarcodeScanEvent {
  id: number
  code: string
  time: string
}

export interface BarcodeScannerPullResponse {
  running: boolean
  connected: boolean
  scannerIp: string
  scannerPort: number
  barcodeRegex: string
  lastError: string
  ioLogs: string[]
  events: BarcodeScanEvent[]
}

export interface BarcodeScannerStatusResponse {
  running: boolean
  connected: boolean
  scannerIp: string
  scannerPort: number
  barcodeRegex: string
  lastError: string
  ioLogs: string[]
}

const baseUrl = '/api/barcodeScanner'

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `HTTP ${response.status}`)
  }
  return response.json() as Promise<T>
}

export function startBarcodeScanner(req: BarcodeScannerStartRequest) {
  return requestJson<BarcodeScannerStatusResponse>(`${baseUrl}/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req)
  })
}

export function stopBarcodeScanner() {
  return requestJson<BarcodeScannerStatusResponse>(`${baseUrl}/stop`, {
    method: 'POST'
  })
}

export function getBarcodeScannerStatus() {
  return requestJson<BarcodeScannerStatusResponse>(`${baseUrl}/status`)
}

export function pullBarcodeScannerEvents(afterId: number) {
  return requestJson<BarcodeScannerPullResponse>(`${baseUrl}/pull?afterId=${afterId}`)
}
