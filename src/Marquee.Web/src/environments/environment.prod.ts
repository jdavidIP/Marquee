// Production build (swapped in for environment.ts via fileReplacements). Relative URLs: the SPA, /api
// and /hubs are all served from one CloudFront origin, so the browser never makes a cross-origin call.
export const environment = {
  apiBase: '/api',
  hubUrl: '/hubs/premieres',
  scopeId: 'global',
  fallbackPollIntervalMs: 10000,
  lobbyPollIntervalMs: 4000,
};
