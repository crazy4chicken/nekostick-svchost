// Monaco editor loaded from a public CDN at runtime (AMD loader build).
// Keeping monaco out of the bundle preserves the single-file WebUI artifact;
// the loader build self-hosts its cross-origin web workers since 0.52.

const MONACO_VERSION = '0.56.0'
const MONACO_BASE = `https://cdn.jsdelivr.net/npm/monaco-editor@${MONACO_VERSION}/min/vs`

declare global {
  interface Window {
    monaco?: MonacoApi
    require?: {
      config(options: { paths: Record<string, string> }): void
      (modules: string[], onload: () => void, onerror?: (err: unknown) => void): void
    }
  }
}

export interface MonacoTextModel {
  getValue(): string
  getOffsetAt(position: { lineNumber: number; column: number }): number
  getPositionAt(offset: number): { lineNumber: number; column: number }
  getLineContent(lineNumber: number): string
  getLineCount(): number
  getWordUntilPosition(position: { lineNumber: number; column: number }): {
    startColumn: number
    endColumn: number
    word: string
  }
}

export interface MonacoEditor {
  getValue(): string
  setValue(value: string): void
  getModel(): MonacoTextModel | null
  onDidChangeModelContent(listener: () => void): { dispose(): void }
  trigger(source: string, handlerId: string): void
  dispose(): void
}

export interface MonacoMarker {
  severity: number
  message: string
  startLineNumber: number
  startColumn: number
  endLineNumber: number
  endColumn: number
}

export interface MonacoCompletionItem {
  label: string
  kind: number
  insertText: string
  detail?: string
  documentation?: string
  sortText?: string
  range?: unknown
}

export interface MonacoCompletionContext {
  triggerKind: number
}

export interface MonacoApi {
  editor: {
    create(element: HTMLElement, options: Record<string, unknown>): MonacoEditor
    setModelMarkers(model: MonacoTextModel, owner: string, markers: MonacoMarker[]): void
  }
  languages: {
    registerCompletionItemProvider(
      languageId: string,
      provider: {
        triggerCharacters?: string[]
        provideCompletionItems(
          model: MonacoTextModel,
          position: { lineNumber: number; column: number },
        ): { suggestions: MonacoCompletionItem[] }
      },
    ): { dispose(): void }
    CompletionItemKind: { Field: number; Value: number }
  }
  // Real monaco exposes MarkerSeverity on the top-level namespace.
  MarkerSeverity: { Error: number; Warning: number }
  Range: new (
    startLineNumber: number,
    startColumn: number,
    endLineNumber: number,
    endColumn: number,
  ) => unknown
}

let loaderPromise: Promise<MonacoApi> | null = null

/** Loads monaco from the CDN exactly once; rejects when the CDN is unreachable. */
export function loadMonaco(): Promise<MonacoApi> {
  loaderPromise ??= (async () => {
    if (window.monaco) {
      return window.monaco
    }

    const scriptReady = Promise.withResolvers<void>()
    const script = document.createElement('script')
    script.src = `${MONACO_BASE}/loader.js`
    script.onload = () => scriptReady.resolve()
    script.onerror = () => scriptReady.reject(new Error('Failed to load monaco loader from CDN'))
    document.head.appendChild(script)
    await scriptReady.promise

    const amdRequire = window.require
    if (!amdRequire) {
      throw new Error('Monaco loader did not initialize')
    }

    amdRequire.config({ paths: { vs: MONACO_BASE } })
    const editorReady = Promise.withResolvers<MonacoApi>()
    amdRequire(
      ['vs/editor/editor.main'],
      () =>
        window.monaco
          ? editorReady.resolve(window.monaco)
          : editorReady.reject(new Error('Monaco editor.main missing')),
      editorReady.reject,
    )
    return editorReady.promise
  })()
  return loaderPromise
}
