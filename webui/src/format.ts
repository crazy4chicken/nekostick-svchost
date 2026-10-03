// Compact timestamp helpers shared by dashboard/config views.

export function relativeTime(iso?: string | null): string {
  if (!iso) return '—'
  const time = Date.parse(iso)
  if (Number.isNaN(time)) return '—'
  const seconds = Math.round((Date.now() - time) / 1000)
  if (seconds < 0) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  if (days < 30) return `${days}d ago`
  return new Date(time).toLocaleDateString()
}

export function absoluteTime(iso?: string | null): string {
  if (!iso) return ''
  const time = Date.parse(iso)
  return Number.isNaN(time) ? '' : new Date(time).toLocaleString()
}
