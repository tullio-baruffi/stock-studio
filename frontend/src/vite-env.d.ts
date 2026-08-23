/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Origin of the API when it is hosted separately from the SPA. Empty = same origin. */
  readonly VITE_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}