export const environment = {
  apiBase: 'http://localhost:5080/api',
  // The live Premiere feed (iteration 3). Clap counts arrive here in batches rather than by
  // polling; the REST endpoints are only used for the initial load and for clapping.
  hubUrl: 'http://localhost:5080/hubs/premieres',
  // v1 is global-only, but the scope travels with every join so scoped Premieres are additive.
  scopeId: 'global',
  // Safety net only: if the socket drops and cannot reconnect, fall back to a slow poll so the
  // page still moves. The socket is the primary path.
  fallbackPollIntervalMs: 10000,
};
