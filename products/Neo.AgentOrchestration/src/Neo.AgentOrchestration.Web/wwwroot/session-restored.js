// Only a same-origin completion signal; no tokens, identity or task data.
if (window.opener) {
    window.opener.postMessage({ type: 'fanasa:session-restored' }, location.origin);
    window.close();
}
