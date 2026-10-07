// Production build (swapped in for environment.ts via fileReplacements). Relative URLs: the SPA, /api
// and /hubs are all served from one CloudFront origin, so the browser never makes a cross-origin call.
export const environment = {
  apiBase: '/api',
  hubUrl: '/hubs/premieres',
  scopeId: 'global',
  fallbackPollIntervalMs: 10000,
  lobbyPollIntervalMs: 4000,
  // MarqueeAuthStack's pool (DEPLOYMENT.md §2a). Written here rather than injected at build time:
  // neither value is secret — the client id is public by design — and the pool is retained, so they
  // do not change. If the pool is ever replaced, these and the API's Cognito__* settings change with it.
  cognito: {
    endpoint: 'https://cognito-idp.ca-central-1.amazonaws.com',
    clientId: '20ljh9mr0pc2hpjbriduqqtp1t',
  },
};
