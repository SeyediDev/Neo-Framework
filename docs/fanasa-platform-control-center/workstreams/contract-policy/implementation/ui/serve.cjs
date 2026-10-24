'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const root = __dirname;
const port = Number(process.env.CONTRACT_UI_PORT || 4186);
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.png': 'image/png', '.json': 'application/json; charset=utf-8' };
const server = http.createServer((request, response) => {
  if (request.url === '/api/workbench' || request.url.startsWith('/api/workbench/')) {
    const upstream = http.request({hostname:'127.0.0.1',port:Number(process.env.WORKBENCH_PORT || 4187),path:request.url,method:request.method,headers:request.headers}, reply => {
      response.writeHead(reply.statusCode, {...reply.headers,'Cache-Control':'no-store'});
      reply.pipe(response);
    });
    upstream.on('error', () => { if (!response.headersSent) response.writeHead(503, {'Content-Type':'application/json; charset=utf-8'}); response.end(JSON.stringify({error:'workbench-unavailable',message:'موتور محلی در دسترس نیست؛ سرویس کارگاه را اجرا کنید.'})); });
    request.pipe(upstream);
    return;
  }
  if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405, { Allow: 'GET, HEAD' }).end(); return; }
  let pathname;
  try { pathname = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname); }
  catch { response.writeHead(400).end(); return; }
  const filename = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
  if (pathname.startsWith('/server/')) { response.writeHead(403).end(); return; }
  if (!filename.startsWith(root + path.sep)) { response.writeHead(403).end(); return; }
  fs.readFile(filename, (error, data) => {
    if (error) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'Content-Type': types[path.extname(filename)] || 'application/octet-stream', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    response.end(request.method === 'HEAD' ? undefined : data);
  });
});
server.on('error', error => { console.error(error.message); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => console.log(`Contract Policy UI demo: http://127.0.0.1:${server.address().port}/`));
