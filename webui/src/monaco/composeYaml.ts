// Compose-aware YAML support for the monaco editor: structural validation that
// mirrors ComposeFileParser.cs plus schema-driven key/value completion.
// The `yaml` parser library is loaded from a public CDN at runtime, so the
// single-file WebUI bundle stays free of editor/parser weight.

import type {
  MonacoApi,
  MonacoCompletionItem,
  MonacoEditor,
  MonacoMarker,
  MonacoPosition,
  MonacoTextModel,
} from './loader'
const YAML_LIB_URL = 'https://cdn.jsdelivr.net/npm/yaml@2.9.1/+esm'
const MARKER_OWNER = 'svchost-compose'

// Rule mirrors: src/Nekostick.ServiceHost/Compose/ComposeFileParser.Validation.cs
const SERVICE_NAME = /^[a-z0-9][a-z0-9-]{0,62}$/
const SHA256 = /^[0-9a-fA-F]{64}$/
const RELEASE_SOURCE = /^[a-z0-9][a-z0-9-]*:.+$/
const DURATION = /^[0-9]+(\.[0-9]+)?(ms|s|m|h)$/i
const START_MODES = ['eager', 'lazy']
const RESTART_POLICIES = ['never', 'on-failure', 'always']
const HEALTH_TYPES = ['process', 'tcp', 'http']
const SERVICE_SCOPES = ['global', 'document']

const ROOT_KEYS = ['services', 'serviceScope', 'strictSources']
const SERVICE_KEYS = ['source', 'args', 'env', 'start', 'restart', 'health', 'route']
const SOURCE_KEYS = ['url', 'path', 'release', 'sha256']
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
  const release = scalarString(yaml, entries['release']?.value ?? null)
  const declaredSourceCount =
    (url !== null && url !== '' ? 1 : 0) +
    (localPath !== null && localPath !== '' ? 1 : 0) +
    (release !== null && release !== '' ? 1 : 0)
  if (declaredSourceCount !== 1) {
    markers.push(
      markerAt(model, pair.value ?? pair.key,
      `${path} must declare exactly one of url, path, or release.`,
      monaco.MarkerSeverity.Error,),
    )
  }

  if (release !== null && release !== '' && !RELEASE_SOURCE.test(release)) {
    markers.push(
      markerAt(model, entries['release']?.value ?? entries['release']?.key ?? pair.value ?? pair.key,
      "release must use '{provider}:{spec}' with a lowercase provider key and non-empty spec.",
      monaco.MarkerSeverity.Error,),
    )
  }

  const shaPair = entries['sha256']
  const sha256 = scalarString(yaml, shaPair?.value ?? null)
  if (sha256 === null || sha256 === '') {
    const message = strictSources
      ? 'URL, path, and release sources must declare sha256 when strictSources is true.'
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
  validateEnum(monaco, model, yaml, root['serviceScope'], 'serviceScope', SERVICE_SCOPES, markers)

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

interface KeyDef {
  doc: string
  block?: boolean
}

const KEY_DEFS: Record<string, Record<string, KeyDef>> = {
  '': {
    services: { doc: 'Service definitions keyed by name (^[a-z0-9][a-z0-9-]{0,62}$).', block: true },
    serviceScope: { doc: 'Service namespace: shared across config files (global) or local to this config file (document).' },
    strictSources: { doc: 'When true, url/path/release sources must declare a sha256 digest.' },
  },
  'services.*': {
    source: { doc: 'Executable source: exactly one of url, path, or release, optional sha256 pinning.', block: true },
    args: { doc: 'Process arguments as a YAML sequence.' },
    env: { doc: 'Environment variable overrides (KEY: value).', block: true },
    start: { doc: 'When to start: eager (with the config) or lazy (first request).' },
    restart: { doc: 'Restart policy: never, on-failure, or always.' },
    health: { doc: 'Health check: process liveness, loopback TCP, or HTTP.', block: true },
    route: { doc: 'Optional HTTP route declaration.', block: true },
  },
  'services.*.source': {
    url: { doc: 'HTTPS URL to download the executable from.' },
    path: { doc: 'Node-local filesystem path of the executable.' },
    release: { doc: "Provider release spec using 'provider:spec', e.g. github:owner/repo." },
    sha256: { doc: 'Expected SHA-256 of the source content (64 hex characters).' },
  },
  'services.*.health': {
    type: { doc: 'Check mechanism: process, tcp, or http.' },
    path: { doc: 'HTTP health-check path, e.g. /healthz.' },
    timeout: { doc: 'Check timeout: 500ms, 5s, 2m, 1h.' },
  },
  'services.*.route': {
    prefix: { doc: 'Path prefix to match, e.g. /api/demo.' },
    strip: { doc: 'Strip the prefix before forwarding.' },
    methods: { doc: 'Restrict to these HTTP methods.' },
    hosts: { doc: 'Restrict to these Host header values.' },
  },
}

const VALUE_DEFS: Record<string, { value: string; doc: string }[]> = {
  strictSources: [
    { value: 'true', doc: 'Sources without sha256 are rejected.' },
    { value: 'false', doc: 'Sources without sha256 only produce a warning.' },
  ],
  serviceScope: [
    { value: 'global', doc: 'Share service names across config files.' },
    { value: 'document', doc: 'Keep service names local to this config file.' },
  ],
  start: [
    { value: 'eager', doc: 'Start as soon as the configuration is applied.' },
    { value: 'lazy', doc: 'Start on the first incoming request.' },
  ],
  restart: [
    { value: 'never', doc: 'Never restart after exit.' },
    { value: 'on-failure', doc: 'Restart only after a failed exit.' },
    { value: 'always', doc: 'Restart after every exit.' },
  ],
  type: [
    { value: 'process', doc: 'Check that the child process is alive.' },
    { value: 'tcp', doc: 'Probe a loopback TCP endpoint.' },
    { value: 'http', doc: 'GET a loopback HTTP path (requires path).' },
  ],
  strip: [
    { value: 'true', doc: 'Remove the route prefix before forwarding.' },
    { value: 'false', doc: 'Forward the original request path.' },
  ],
  timeout: [
    { value: '500ms', doc: 'Sub-second probe timeout.' },
    { value: '5s', doc: 'The parser default.' },
    { value: '30s', doc: 'Slow service startup tolerance.' },
    { value: '1m', doc: 'Very slow dependency tolerance.' },
  ],
}

function provideCompletions(
  monaco: MonacoApi,
  model: MonacoTextModel,
  position: { lineNumber: number; column: number },
): { suggestions: MonacoCompletionItem[] } {
  const line = model.getLineContent(position.lineNumber)
  // Letter trigger characters bypass quickSuggestions gating, so suppress
  // comments explicitly here.
  if (line.trimStart().startsWith('#')) {
    return { suggestions: [] }
  }
  const beforeCursor = line.slice(0, position.column - 1)
  const indent = /^\s*/.exec(line)?.[0].length ?? 0
  const word = model.getWordUntilPosition(position)
  const wordRange = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn)

  // Value completion: "key: <cursor>"
  const valueMatch = /^\s*([A-Za-z][^:#]*):\s*([^:#]*)$/.exec(beforeCursor)
  if (valueMatch) {
    const values = VALUE_DEFS[valueMatch[1].trim()]
    if (!values) {
      return { suggestions: [] }
    }
    const valueStart = beforeCursor.length - valueMatch[2].length + 1
    const range = new monaco.Range(position.lineNumber, valueStart, position.lineNumber, position.column)
    return {
      suggestions: values.map((entry, index) => ({
        label: entry.value,
        kind: monaco.languages.CompletionItemKind.Value,
        insertText: entry.value,
        detail: 'allowed value',
        documentation: entry.doc,
        sortText: String(index).padStart(2, '0'),
        range,
      })),
    }
  }

  // Key completion based on the mapping path above the cursor.
  const defs = KEY_DEFS[docPath(keyPathAbove(model, position.lineNumber, indent))] ?? {}
  return {
    suggestions: Object.entries(defs).map(([key, def], index) => ({
      label: key,
      kind: monaco.languages.CompletionItemKind.Field,
      // The editor's auto-indent already repeats the current indent after \n;
      // add only the two-space child level for block keys.
      insertText: def.block ? `${key}:\n  ` : `${key}: `,
      detail: 'compose key',
      documentation: def.doc,
      sortText: String(index).padStart(2, '0'),
      range: wordRange,
    })),
  }
}

/** Maps a concrete key path (services.demo-api.source) to its schema path (services.*.source). */
function docPath(path: string[]): string {
  return path.map((segment, index) => (path[0] === 'services' && index === 1 ? '*' : segment)).join('.')
}

function provideHover(
  monaco: MonacoApi,
  model: MonacoTextModel,
  position: MonacoPosition,
): { contents: { value: string }[]; range?: unknown } | null {
  const line = model.getLineContent(position.lineNumber)
  const pairMatch = /^(\s*)([A-Za-z][^:#]*):\s*([^:#]*)$/.exec(line)
  if (!pairMatch) {
    return null
  }
  const indent = pairMatch[1].length
  const key = pairMatch[2].trim()
  const keyEnd = indent + 1 + key.length

  if (position.column <= keyEnd) {
    // Hovering the key: show the field documentation.
    const path = keyPathAbove(model, position.lineNumber, indent)
    const doc = KEY_DEFS[docPath(path)]?.[key]?.doc
    if (!doc) {
      return null
    }
    return {
      contents: [{ value: `**\`${key}\`** — ${doc}` }],
      range: new monaco.Range(position.lineNumber, indent + 1, position.lineNumber, keyEnd),
    }
  }

  // Hovering a value: show the hovered enum member's documentation.
  const word = model.getWordAtPosition(position)
  const entry = word ? VALUE_DEFS[key]?.find((candidate) => candidate.value === word.word) : undefined
  if (!entry || !word) {
    return null
  }
  return {
    contents: [{ value: `**\`${entry.value}\`** — ${entry.doc}` }],
    range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
  }
}

let supportRegistered = false

/** Registers compose completion/hover once and attaches debounced validation plus newline auto-suggest. */
export async function attachComposeSupport(monaco: MonacoApi, editor: MonacoEditor): Promise<void> {
  const model = editor.getModel()
  if (!model) {
    return
  }
  const yaml = await loadYamlLib()

  if (!supportRegistered) {
    supportRegistered = true
    // Letter triggers: monaco 0.56 quickSuggestions does not consult providers
    // while typing (verified empirically); declared trigger characters do.
    // Returning empty suggestions for non-key positions keeps this silent.
    const letters = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('')
    monaco.languages.registerCompletionItemProvider('yaml', {
      triggerCharacters: [':', ' ', ...letters],
      provideCompletionItems: (m, position) => provideCompletions(monaco, m, position),
    })
    monaco.languages.registerHoverProvider('yaml', {
      provideHover: (m, position) => provideHover(monaco, m, position),
    })
  }

  let timer = 0
  const runValidation = () => {
    monaco.editor.setModelMarkers(model, MARKER_OWNER, validateDocument(monaco, model, yaml, model.getValue()))
  }
  editor.onDidChangeModelContent(() => {
    clearTimeout(timer)
    timer = window.setTimeout(runValidation, 300)

    // Offer completions after every edit — including accepted suggestions, whose
    // insertion is itself a content change — instead of relying on trigger
    // characters alone. Empty suggestion lists keep the widget hidden.
    window.setTimeout(() => {
      editor.trigger('svchost.edit', 'editor.action.triggerSuggest')
    }, 10)
  })
  runValidation()
}
