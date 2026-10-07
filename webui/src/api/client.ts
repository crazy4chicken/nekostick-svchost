import type {
  ActionResponse,
  ConfigDetail,
  ConfigSummary,
  ErrorBody,
  LogPage,
  ManagedService,
  ServiceDeclSummary,
  ServiceDecision,
  ServiceSyncResult,
  SettingsResponse,
  SettingsUpdate,
  StatusResponse,
  SyncReport,
} from './types'

const BASE = '/svchost/api'
const KEY_STORAGE = 'svchost.apiKey'

export class ApiError extends Error {
  readonly code: string
  readonly status: number

  constructor(code: string, message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
  }
}

export function getStoredKey(): string | null {
  return localStorage.getItem(KEY_STORAGE)
}

export function setStoredKey(key: string): void {
  localStorage.setItem(KEY_STORAGE, key)
}

export function clearStoredKey(): void {
  localStorage.removeItem(KEY_STORAGE)
}

async function request<T>(
  method: string,
  path: string,
  body?: unknown,
  opts?: { presentedKey?: string },
): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const key = opts?.presentedKey ?? getStoredKey()
  if (key) headers['X-Api-Key'] = key

  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError('Unreachable', 'Management API is unreachable.', 0)
  }

  if (!res.ok) {
    // Non-2xx is always a request-level failure carrying the error envelope;
    // reconcile-level failures arrive as HTTP 200 with a report payload.
    let code = 'Error'
    let message = `Request failed (HTTP ${res.status}).`
    try {
      const parsed = (await res.json()) as ErrorBody
      code = parsed.error?.code ?? code
      message = parsed.error?.message ?? message
    } catch {
      // non-JSON error body; keep the defaults
    }
    throw new ApiError(code, message, res.status)
  }

  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

// Tolerant normalizers: accept the wire shape plus a few legacy variants
// rather than failing hard on field drift.

function asRecord(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {}
}

function normalizeSettings(raw: unknown): SettingsResponse {
  const rec = asRecord(raw)
  const providers = asRecord(rec.releaseProviders)
  const releaseProviders: SettingsResponse['releaseProviders'] = {}
  for (const [providerKey, value] of Object.entries(providers)) {
    const provider = asRecord(value)
    releaseProviders[providerKey] = {
      mirrors: Array.isArray(provider.mirrors) ? provider.mirrors.map((mirror) => String(mirror)) : [],
    }
  }
  return { ...rec, releaseProviders }
}

function normalizeServices(raw: unknown): ServiceDeclSummary[] {
  if (!Array.isArray(raw)) return []
  return raw.map((item) => {
    if (typeof item === 'string') return { name: item }
    const rec = asRecord(item)
    return {
      name: String(rec.name ?? rec.service ?? 'unnamed'),
      source: rec.source === undefined ? undefined : String(rec.source),
    }
  })
}

function normalizeServiceDecision(value: unknown): ServiceDecision | undefined {
  switch (value) {
    case 'reused':
    case 'updated':
    case 'preserved':
    case 'failed':
    case 'skipped':
    case 'removalPending':
      return value
    default:
      return undefined
  }
}

function toServiceSyncResult(name: string, value: unknown): ServiceSyncResult {
  const entry = asRecord(value)
  const error = entry.error ?? entry.reason
  return {
    name,
    succeeded: Boolean(entry.succeeded ?? entry.success ?? entry.ok),
    error: error == null ? undefined : String(error),
    errorKind: entry.errorKind == null ? undefined : String(entry.errorKind),
    decision: normalizeServiceDecision(entry.decision),
    nodeLocal: Boolean(entry.nodeLocal),
    warnings: Array.isArray(entry.warnings) ? entry.warnings.map((warning) => String(warning)) : [],
  }
}

export function normalizeSyncReport(raw: unknown): SyncReport {
  const rec = asRecord(raw)
  const services: ServiceSyncResult[] = []
  const rawServices = rec.services
  if (Array.isArray(rawServices)) {
    for (const item of rawServices) {
      const entry = asRecord(item)
      services.push(
        toServiceSyncResult(String(entry.name ?? entry.service ?? 'unnamed'), entry),
      )
    }
  } else {
    for (const [name, value] of Object.entries(asRecord(rawServices))) {
      services.push(toServiceSyncResult(name, value))
    }
  }
  return {
    name: rec.name == null ? undefined : String(rec.name),
    succeeded: rec.succeeded === undefined ? services.every((s) => s.succeeded) : Boolean(rec.succeeded),
    services,
    dataDirectoryAvailable:
      typeof rec.dataDirectoryAvailable === 'boolean' ? rec.dataDirectoryAvailable : undefined,
    completedAt: rec.completedAt == null ? undefined : String(rec.completedAt),
    notes: Array.isArray(rec.notes) ? rec.notes.map((note) => String(note)) : [],
    error: rec.error == null ? undefined : String(rec.error),
    errorKind: rec.errorKind == null ? undefined : String(rec.errorKind),
  }
}

export function syncReportEntries(report: SyncReport | null): ServiceSyncResult[] {
  return report?.services ?? []
}

function normalizeConfigSummary(raw: unknown): ConfigSummary {
  const rec = asRecord(raw)
  const serviceScope =
    typeof rec.serviceScope === 'string' ? rec.serviceScope.toLowerCase() : null
  return {
    name: String(rec.name ?? 'unnamed'),
    serviceScope: serviceScope === 'global' || serviceScope === 'document' ? serviceScope : null,
    strictSources: typeof rec.strictSources === 'boolean' ? rec.strictSources : null,
    services: normalizeServices(rec.services),
    lock: rec.lock ? (asRecord(rec.lock) as ConfigSummary['lock']) : null,
    lastSync: rec.lastSync ? normalizeSyncReport(rec.lastSync) : null,
  }
}

function normalizeManagedService(raw: unknown): ManagedService {
  const rec = asRecord(raw)
  return {
    config: String(rec.config ?? ''),
    service: String(rec.service ?? rec.name ?? 'unnamed'),
    serviceId: rec.serviceId == null ? undefined : String(rec.serviceId),
    enabled: rec.enabled === undefined ? undefined : Boolean(rec.enabled),
    state: rec.state === undefined ? undefined : String(rec.state),
    detail: rec.detail === undefined ? undefined : String(rec.detail),
  }
}

export const api = {

  getSettings: () =>
    request<unknown>('GET', '/settings').then(normalizeSettings),

  putSettings: (settings: SettingsUpdate) =>
    request<unknown>('PUT', '/settings', settings).then(normalizeSettings),
  getStatus: () => request<StatusResponse>('GET', '/status'),

  setBootstrapKey: (bootstrapKey: string, apiKey: string) =>
    request<StatusResponse>('POST', '/bootstrap/key', { apiKey }, { presentedKey: bootstrapKey }),

  listConfigs: () =>
    request<unknown[]>('GET', '/configs').then((rows) => rows.map(normalizeConfigSummary)),

  getConfig: (name: string) =>
    request<unknown>('GET', `/configs/${encodeURIComponent(name)}`).then((raw) => {
      const rec = asRecord(raw)
      return {
        name: String(rec.name ?? name),
        yaml: String(rec.yaml ?? ''),
        lock: rec.lock ? (asRecord(rec.lock) as ConfigDetail['lock']) : null,
        lastSync: rec.lastSync ? normalizeSyncReport(rec.lastSync) : null,
      } satisfies ConfigDetail
    }),

  putConfig: (name: string, yaml: string) =>
    request<unknown>('PUT', `/configs/${encodeURIComponent(name)}`, { yaml }).then(
      normalizeSyncReport,
    ),

  deleteConfig: (name: string) =>
    request<unknown>('DELETE', `/configs/${encodeURIComponent(name)}`).then((raw) => {
      const rec = asRecord(raw)
      return {
        deleted: Boolean(rec.deleted ?? true),
        name: String(rec.name ?? name),
        report: normalizeSyncReport(rec.report ?? raw),
      }
    }),

  syncConfig: (name: string) =>
    request<unknown>('POST', `/configs/${encodeURIComponent(name)}/sync`).then(
      normalizeSyncReport,
    ),

  listServices: () =>
    request<unknown>('GET', '/services').then((raw) => {
      const rec = asRecord(raw)
      const rows = Array.isArray(rec.services) ? rec.services : Array.isArray(raw) ? raw : []
      return rows.map(normalizeManagedService)
    }),

  serviceAction: (config: string, service: string, action: 'start' | 'stop' | 'restart') =>
    request<unknown>(
      'POST',
      `/services/${encodeURIComponent(config)}/${encodeURIComponent(service)}/${action}`,
    ).then((raw): ActionResponse => {
      const rec = asRecord(raw)
      return {
        action: String(rec.action ?? action),
        config: String(rec.config ?? config),
        service: String(rec.service ?? service),
        succeeded: Boolean(rec.succeeded),
        message: String(rec.message ?? ''),
        asynchronous: Boolean(rec.asynchronous),
      }
    }),
  listLogPage: (config: string, service: string, file: number) =>
    request<LogPage>(
      'GET',
      `/configs/${encodeURIComponent(config)}/services/${encodeURIComponent(service)}/logs?file=${encodeURIComponent(String(file))}`,
    ),

  streamLogTail: async (
    config: string,
    service: string,
    fromLine: number,
    onLine: (line: string) => void,
    signal: AbortSignal,
  ): Promise<void> => {
    const headers: Record<string, string> = { Accept: 'text/event-stream' }
    const key = getStoredKey()
    if (key) headers['X-Api-Key'] = key

    const query = encodeURIComponent(String(fromLine))
    const res = await fetch(
      `${BASE}/configs/${encodeURIComponent(config)}/services/${encodeURIComponent(service)}/logs/tail?fromLine=${query}`,
      { headers, signal },
    )

    if (!res.ok) {
      let code = 'Error'
      let message = `Request failed (HTTP ${res.status}).`
      try {
        const parsed = (await res.json()) as ErrorBody
        code = parsed.error?.code ?? code
        message = parsed.error?.message ?? message
      } catch {
        // non-JSON error body; keep the defaults
      }
      throw new ApiError(code, message, res.status)
    }

    if (!res.body) throw new Error('Log stream response has no body.')

    const reader = res.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    let ended = false

    const consumeLine = (line: string) => {
      if (!line || line.startsWith(':') || !line.startsWith('data:')) return
      const payload = line.slice(5).replace(/^ /, '')
      const event = JSON.parse(payload) as { line?: unknown } | null
      if (typeof event?.line !== 'string') throw new Error('Invalid log stream event payload.')
      onLine(event.line)
    }

    try {
      while (true) {
        const { value, done } = await reader.read()
        if (done) {
          ended = true
          buffer += decoder.decode()
          break
        }

        buffer += decoder.decode(value, { stream: true })
        let newline = buffer.indexOf('\n')
        while (newline >= 0) {
          consumeLine(buffer.slice(0, newline).replace(/\r$/, ''))
          buffer = buffer.slice(newline + 1)
          newline = buffer.indexOf('\n')
        }
      }

      if (buffer) consumeLine(buffer.replace(/\r$/, ''))
    } finally {
      if (!ended) {
        try {
          await reader.cancel()
        } catch {
          // The stream may already have errored or been aborted.
        }
      }
      reader.releaseLock()
    }
  },

}
