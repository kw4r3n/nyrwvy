// OpenCode's public API names HeadersInit, which Node 22 exposes only through
// RequestInit. Use the real Node fetch contract without adding browser globals.
export {}

declare global {
  type HeadersInit = NonNullable<RequestInit["headers"]>
}
