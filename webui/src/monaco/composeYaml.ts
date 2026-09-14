// Compose-aware YAML support for the monaco editor: structural validation that
// mirrors ComposeFileParser.cs plus schema-driven key/value completion.
// The `yaml` parser library is loaded from a public CDN at runtime, so the
// single-file WebUI bundle stays free of editor/parser weight.

import type {
  MonacoApi,
  MonacoCompletionItem,
  MonacoMarker,
  MonacoTextModel,
} from './loader'

const YAML_LIB_URL = 'https://cdn.jsdelivr.net/npm/yaml@2.9.1/+esm'
const MARKER_OWNER = 'svchost-compose'

// Rule mirrors: src/Nekolla.Nekostick.ServiceHost/Compose/ComposeFileParser.Validation.cs
const SERVICE_NAME = /^[a-z0-9][a-z0-9-]{0,62}$/
const SHA256 = /^[0-9a-fA-F]{64}$/
const DURATION = /^[0-9]+(\.[0-9]+)?(ms|s|m|h)$/i
const START_MODES = ['eager', 'lazy']
const RESTART_POLICIES = ['never', 'on-failure', 'always']
const HEALTH_TYPES = ['process', 'tcp', 'http']

const ROOT_KEYS = ['services', 'strictSources']
const SERVICE_KEYS = ['source', 'args', 'env', 'start', 'restart', 'health', 'route']
const SOURCE_KEYS = ['url', 'path', 'sha256']
const HEALTH_KEYS = ['type', 'path', 'timeout']
const ROUTE_KEYS = ['prefix', 'strip', 'methods', 'hosts']

interface YamlNodeLike {
  range?: [number, number, number] | null
}
interface YamlScalarLike extends YamlNodeLike {
  value: unknown
}
interface YamlPairLike {
  key: YamlScalarLike | null
  value: YamlNodeLike | null
}
interface YamlCollectionLike extends YamlNodeLike {
  items: unknown[]
}
interface YamlLib {
  parseDocument(text: string): {
    errors: { message: string; linePos?: [{ line: number; col: number }] }[]
    contents: YamlNodeLike | null
  }
  isMap(node: unknown): node is YamlCollectionLike
  isSeq(node: unknown): node is YamlCollectionLike
  isScalar(node: unknown): node is YamlScalarLike
}

let yamlLibPromise: Promise<YamlLib> | null = null

function loadYamlLib(): Promise<YamlLib> {
  yamlLibPromise ??= import(/* @vite-ignore */ YAML_LIB_URL) as Promise<YamlLib>
  return yamlLibPromise
}

function pairRange(model: MonacoTextModel, node: YamlNodeLike | null | undefined) {
  const fallback = { lineNumber: 1, column: 1 }
  const start = node?.range ? model.getPositionAt(node.range[0]) : fallback
  const end = node?.range ? model.getPositionAt(node.range[1]) : start
  return { start, end }
}

function markerAt(
  model: MonacoTextModel,
  node: YamlNodeLike | null | undefined,
  message: string,
  severity: number,
): MonacoMarker { const { start, end } = pairRange(model, node)
return {
  severity,
  message,
  startLineNumber: start.lineNumber,
  startColumn: start.column,
  endLineNumber: end.lineNumber,
  endColumn: Math.max(end.column, start.column + 1),
} }

function validateMap(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  node: YamlNodeLike | null,
  path: string,
  allowed: string[],
  markers: MonacoMarker[],
): Record<string, YamlPairLike> | null {
  if (node === null) {
    return null
  }
  if (!yaml.isMap(node)) {
    markers.push(markerAt(model, node, `${path} must be a mapping.`, monaco.MarkerSeverity.Error))
    return null
  }
  const entries: Record<string, YamlPairLike> = {}
  for (const item of node.items) {
    const pair = item as YamlPairLike
    const key = pair.key?.value
    if (typeof key !== 'string') {
      markers.push(markerAt(model, pair.key, `${path} keys must be strings.`, monaco.MarkerSeverity.Error))
      continue
    }
    if (!allowed.includes(key)) {
      markers.push(
        markerAt(model, pair.key, `Unknown key "${key}" at ${path}.`, monaco.MarkerSeverity.Error),
      )
      continue
    }
    entries[key] = pair
  }
  return entries
}

function scalarString(yaml: YamlLib, node: YamlNodeLike | null | undefined): string | null {
  return yaml.isScalar(node) && node.value !== null && node.value !== undefined ? String(node.value) : null
}

function validateScalar(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  path: string,
  markers: MonacoMarker[],
): string | null {
  // Absent optional keys are fine; only an existing non-scalar value errors.
  if (!pair) {
    return null
  }
  const value = scalarString(yaml, pair.value ?? null)
  if (value === null) {
    markers.push(
      markerAt(model, pair.value ?? pair.key, `${path} must be a scalar value.`, monaco.MarkerSeverity.Error),
    )
  }
  return value
}

function validateSequence(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  path: string,
  markers: MonacoMarker[],
): void {
  if (!pair?.value) {
    return
  }
  if (!yaml.isSeq(pair.value)) {
    markers.push(markerAt(model, pair.value, `${path} must be a sequence.`, monaco.MarkerSeverity.Error))
    return
  }
  for (const item of pair.value.items) {
    if (!yaml.isScalar(item)) {
      markers.push(markerAt(model, item as YamlNodeLike, `${path} entries must be scalar values.`, monaco.MarkerSeverity.Error))
    }
  }
}

function validateEnum(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  path: string,
  allowed: string[],
  markers: MonacoMarker[],
): void {
  const value = validateScalar(monaco, model, yaml, pair, path, markers)
  if (value !== null && !allowed.includes(value.toLowerCase())) {
    markers.push(
      markerAt(model, pair?.value ?? pair?.key,
      `${path} must be one of: ${allowed.join(', ')}.`,
      monaco.MarkerSeverity.Error,),
    )
  }
}

function validateSource(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  serviceName: string,
  strictSources: boolean,
  markers: MonacoMarker[],
): void {
  const path = `services.${serviceName}.source`
  if (!pair) {
    return
  }
  const entries = validateMap(monaco, model, yaml, pair.value ?? null, path, SOURCE_KEYS, markers)
  if (!entries) {
    return
  }

  const url = scalarString(yaml, entries['url']?.value ?? null)
  const localPath = scalarString(yaml, entries['path']?.value ?? null)
  if ((url === null || url === '') === (localPath === null || localPath === '')) {
    markers.push(
      markerAt(model, pair.value ?? pair.key,
      `${path} must declare exactly one of url or path.`,
      monaco.MarkerSeverity.Error,),
    )
  }

  const shaPair = entries['sha256']
  const sha256 = scalarString(yaml, shaPair?.value ?? null)
  if (sha256 === null || sha256 === '') {
    const message = strictSources
      ? 'URL and path sources must declare sha256 when strictSources is true.'
      : 'The source does not declare sha256; source content is not pinned by the compose document.'
    markers.push(
      markerAt(model, shaPair?.value ?? pair.value ?? pair.key,
      message,
      strictSources ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,),
    )
  } else if (!SHA256.test(sha256)) {
    markers.push(
      markerAt(model, shaPair?.value ?? shaPair.key,
      'sha256 must contain exactly 64 hexadecimal characters.',
      monaco.MarkerSeverity.Error,),
    )
  }
}

function validateHealth(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  serviceName: string,
  markers: MonacoMarker[],
): void {
  const path = `services.${serviceName}.health`
  if (!pair) {
    return
  }
  const entries = validateMap(monaco, model, yaml, pair.value ?? null, path, HEALTH_KEYS, markers)
  if (!entries) {
    return
  }

  validateEnum(monaco, model, yaml, entries['type'], `${path}.type`, HEALTH_TYPES, markers)
  const type = scalarString(yaml, entries['type']?.value ?? null)?.toLowerCase()
  const checkPath = scalarString(yaml, entries['path']?.value ?? null)
  if (type === 'http' && (checkPath === null || checkPath === '')) {
    markers.push(
      markerAt(model, pair.value ?? pair.key, 'HTTP health checks require a path.', monaco.MarkerSeverity.Error),
    )
  }

  const timeoutPair = entries['timeout']
  const timeout = validateScalar(monaco, model, yaml, timeoutPair, `${path}.timeout`, markers)
  if (timeout !== null && !DURATION.test(timeout)) {
    markers.push(
      markerAt(model, timeoutPair?.value ?? timeoutPair?.key,
      `${path}.timeout must look like 500ms, 5s, 2m, or 1h.`,
      monaco.MarkerSeverity.Error,),
    )
  }
}

function validateRoute(
  monaco: MonacoApi,
  model: MonacoTextModel,
  yaml: YamlLib,
  pair: YamlPairLike | undefined,
  serviceName: string,
  markers: MonacoMarker[],
): void {
  const path = `services.${serviceName}.route`
  if (!pair) {
    return
  }
  const entries = validateMap(monaco, model, yaml, pair.value ?? null, path, ROUTE_KEYS, markers)
  if (!entries) {
    return
  }

  const prefix = scalarString(yaml, entries['prefix']?.value ?? null)
  if (prefix === null || prefix === '') {
    markers.push(
      markerAt(model, pair.value ?? pair.key, `${path}.prefix is required.`, monaco.MarkerSeverity.Error),
    )
  }
  validateSequence(monaco, model, yaml, entries['methods'], `${path}.methods`, markers)
  validateSequence(monaco, model, yaml, entries['hosts'], `${path}.hosts`, markers)
}

function validateDocument(monaco: MonacoApi, model: MonacoTextModel, yaml: YamlLib, text: string): MonacoMarker[] {
  const markers: MonacoMarker[] = []
  const doc = yaml.parseDocument(text)

  for (const error of doc.errors) {
    const pos = error.linePos?.[0]
    markers.push({
      severity: monaco.MarkerSeverity.Error,
      message: error.message,
      startLineNumber: pos?.line ?? 1,
      startColumn: pos?.col ?? 1,
      endLineNumber: pos?.line ?? 1,
      endColumn: (pos?.col ?? 1) + 1,
    })
  }
  if (doc.errors.length > 0 || !doc.contents) {
    return markers
  }

  const root = validateMap(monaco, model, yaml, doc.contents, 'document', ROOT_KEYS, markers)
  if (!root) {
    return markers
  }

  const strictSources = scalarString(yaml, root['strictSources']?.value ?? null)?.toLowerCase() === 'true'

  const servicesPair = root['services']
  if (!servicesPair?.value || !yaml.isMap(servicesPair.value)) {
    if (servicesPair?.value) {
      markers.push(
        markerAt(model, servicesPair.value, 'services must be a mapping.', monaco.MarkerSeverity.Error),
      )
    }
    return markers
  }

  for (const item of servicesPair.value.items) {
    const servicePair = item as YamlPairLike
    const name = servicePair.key?.value
    if (typeof name !== 'string') {
      continue
    }
    if (!SERVICE_NAME.test(name)) {
      markers.push(
        markerAt(model, servicePair.key,
        'Service names must match ^[a-z0-9][a-z0-9-]{0,62}$.',
        monaco.MarkerSeverity.Error,),
      )
    }

    const servicePath = `services.${name}`
    const entries = validateMap(monaco, model, yaml, servicePair.value ?? null, servicePath, SERVICE_KEYS, markers)
    if (!entries) {
      continue
    }
    if (!('source' in entries)) {
      markers.push(
        markerAt(model, servicePair.key, `${servicePath}.source is required.`, monaco.MarkerSeverity.Error),
      )
    }

    validateSource(monaco, model, yaml, entries['source'], name, strictSources, markers)
    validateSequence(monaco, model, yaml, entries['args'], `${servicePath}.args`, markers)
    validateEnum(monaco, model, yaml, entries['start'], `${servicePath}.start`, START_MODES, markers)
    validateEnum(monaco, model, yaml, entries['restart'], `${servicePath}.restart`, RESTART_POLICIES, markers)
    validateHealth(monaco, model, yaml, entries['health'], name, markers)
    validateRoute(monaco, model, yaml, entries['route'], name, markers)
  }

  return markers
}

// Completion context: reconstruct the mapping key path above the cursor by
// indentation; sufficient for the fixed compose document shape.
function keyPathAbove(model: MonacoTextModel, lineNumber: number, indent: number): string[] {
  const stack: { indent: number; key: string }[] = []
  for (let line = 1; line < lineNumber; line++) {
    const content = model.getLineContent(line)
    const match = /^(\s*)([A-Za-z][^:#]*):\s*(?:#.*)?$/.exec(content)
    if (!match) {
      continue
    }
    const lineIndent = match[1].length
    while (stack.length > 0 && stack[stack.length - 1].indent >= lineIndent) {
      stack.pop()
    }
    stack.push({ indent: lineIndent, key: match[2].trim() })
  }
  while (stack.length > 0 && stack[stack.length - 1].indent >= indent) {
    stack.pop()
  }
  return stack.map((entry) => entry.key)
}

const KEY_COMPLETIONS: Record<string, string[]> = {
  '': ROOT_KEYS,
  services: [],
  'services.*': SERVICE_KEYS,
  'services.*.source': SOURCE_KEYS,
  'services.*.health': HEALTH_KEYS,
  'services.*.route': ROUTE_KEYS,
}

const VALUE_COMPLETIONS: Record<string, string[]> = {
  strictSources: ['true', 'false'],
  start: START_MODES,
  restart: RESTART_POLICIES,
  type: HEALTH_TYPES,
  strip: ['true', 'false'],
  timeout: ['500ms', '5s', '30s', '1m'],
}

function provideCompletions(
  monaco: MonacoApi,
  model: MonacoTextModel,
  position: { lineNumber: number; column: number },
): { suggestions: MonacoCompletionItem[] } {
  const line = model.getLineContent(position.lineNumber)
  const beforeCursor = line.slice(0, position.column - 1)
  const indent = /^\s*/.exec(line)?.[0].length ?? 0

  // Value completion: "key: <cursor>"
  const valueMatch = /^\s*([A-Za-z][^:#]*):\s*([^:#]*)$/.exec(beforeCursor)
  if (valueMatch) {
    const key = valueMatch[1].trim()
    const values = VALUE_COMPLETIONS[key]
    if (!values) {
      return { suggestions: [] }
    }
    const valueStart = beforeCursor.length - valueMatch[2].length + 1
    const range = new monaco.Range(position.lineNumber, valueStart, position.lineNumber, position.column)
    return {
      suggestions: values.map((value) => ({
        label: value,
        kind: monaco.languages.CompletionItemKind.Value,
        insertText: value,
        detail: `${key} value`,
        range,
      })),
    }
  }

  // Key completion based on the mapping path above the cursor.
  const path = keyPathAbove(model, position.lineNumber, indent)
  const generalized = path.map((segment, index) => (index >= 1 && path[0] === 'services' && index === 1 ? '*' : segment))
  const lookup = generalized.join('.').replace(/^services\.[^.]+/, 'services.*')
  const keys = KEY_COMPLETIONS[lookup] ?? KEY_COMPLETIONS[generalized.join('.')] ?? []
  const range = new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column)
  return {
    suggestions: keys.map((key) => ({
      label: key,
      kind: monaco.languages.CompletionItemKind.Field,
      insertText: `${key}: `,
      detail: 'compose key',
      range,
    })),
  }
}

let supportRegistered = false

/** Registers compose completion once and attaches debounced validation to the model. */
export async function attachComposeSupport(
  monaco: MonacoApi,
  model: MonacoTextModel,
  onDidChangeModelContent: (listener: () => void) => { dispose(): void },
): Promise<void> {
  const yaml = await loadYamlLib()

  if (!supportRegistered) {
    supportRegistered = true
    monaco.languages.registerCompletionItemProvider('yaml', {
      triggerCharacters: [':', ' '],
      provideCompletionItems: (m, position) => provideCompletions(monaco, m, position),
    })
  }

  let timer: number | null = null
  const runValidation = () => {
    monaco.editor.setModelMarkers(model, MARKER_OWNER, validateDocument(monaco, model, yaml, model.getValue()))
  }
  onDidChangeModelContent(() => {
    if (timer !== null) {
      clearTimeout(timer)
    }
    timer = window.setTimeout(runValidation, 300)
  })
  runValidation()
}
