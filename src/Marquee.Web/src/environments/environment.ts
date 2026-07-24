export const environment = {
  apiBase: 'http://localhost:5080/api',
  // How often the Premiere page re-polls the live clap count (ms). Iteration 3 replaces
  // polling with SignalR; until then this is the "refresh a page you watch" hook.
  pollIntervalMs: 2000,
};
