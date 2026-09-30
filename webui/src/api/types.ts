// Typed shapes for the management API contract (PLAN.md §6 + orchestrator
// contract update: reconcile-level failures return HTTP 200 with a report
// payload; non-2xx is always the { error: { code, message } } envelope).

export interface StatusResponse {
  bootstrap: boolean
  version: string
  // Current backend omits this field (gate review G3-F3); kept optional.
  dataDirectoryAvailable?: boolean
  configs: number
}

export interface ReleaseProviderSettings {
  mirrors: string[]
}

export interface ReleaseProvidersSettings {
  [providerKey: string]: ReleaseProviderSettings
}

export interface SettingsResponse {
  releaseProviders: ReleaseProvidersSettings
  [group: string]: unknown
}

export type SettingsUpdate = Partial<SettingsResponse>

export interface ServiceDeclSummary {
  name: string
  source?: string
}

export interface LockedServiceSummary {
  serviceId?: string
  source?: {
    kind?: string
    url?: string
    path?: string
    providerKey?: string
    spec?: string
    tag?: string
    version?: string
    assetName?: string
    sha256?: string
  }
}

export interface ConfigLockSummary {
  services?: Record<string, LockedServiceSummary>
}

export interface ServiceSyncResult {
  name: string
  succeeded: boolean
  error?: string
  errorKind?: string
  warnings: string[]
}

export interface SyncReport {
  name?: string
  succeeded: boolean
  services: ServiceSyncResult[]
  error?: string
}

export interface ActionResponse {
  action: string
  config: string
  service: string
  succeeded: boolean
  message: string
  asynchronous: boolean
}

export interface DeleteConfigResponse {
  deleted: boolean
  name: string
  report: SyncReport
}

export interface ConfigSummary {
  name: string
  services: ServiceDeclSummary[]
  lock: ConfigLockSummary | null
  lastSync: SyncReport | null
}

export interface ConfigDetail {
  name: string
  yaml: string
  lock: ConfigLockSummary | null
}

export interface ManagedService {
  config: string
  service: string
  serviceId?: string
  enabled?: boolean
  state?: string
  detail?: string
}

export interface ErrorBody {
  error?: { code?: string; message?: string }
}
