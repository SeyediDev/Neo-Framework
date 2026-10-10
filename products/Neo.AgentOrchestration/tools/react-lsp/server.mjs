// Experimental read-only LSP server. No commands, edits, plugins or push diagnostics.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import crypto from 'node:crypto';
import ts from './node_modules/typescript/lib/typescript.js';

if (ts.version !== '5.9.3') throw Error('Unapproved TypeScript version');
const MAX = 4 * 1024 * 1024;
let root, document, service, initialized = false, closing = false, invalid = false;
const tracePath = process.argv[2];
function trace(method, stage, reason) { if (tracePath) fs.appendFileSync(tracePath, JSON.stringify({method,stage,reason,at:Date.now()})+'\n'); }
const files = new Map();
const normalize = value => path.resolve(value).toLowerCase();
const digest = text => crypto.createHash('sha256').update(text, 'utf8').digest('hex');
function localUri(value) {
  const uri = new URL(value);
  if (uri.protocol !== 'file:' || uri.hostname || uri.search || uri.hash) throw Error('Nonlocal URI');
  return fileURLToPath(uri);
}
function noLinks(file) {
  for (let current = file; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw Error('Link rejected');
    if (path.dirname(current) === current) break;
  }
}
function scoped(uri) {
  const file = localUri(uri), relative = path.relative(root, file);
  if (!relative || relative.startsWith('..' + path.sep) || relative === '..' || path.isAbsolute(relative) || relative.includes(':')) throw Error('Foreign scope');
  noLinks(file);
  return file;
}
function snapshot(file) {
  const key = normalize(file);
  if (document && key === normalize(document.file)) return document.text;
  if (files.has(key)) return files.get(key).text;
  noLinks(file);
  const text = ts.sys.readFile(file);
  if (text !== undefined) files.set(key, {file, text, hash: digest(text)});
  return text;
}
function fresh() {
  if (invalid || !document || !service) throw Error('Invalid session');
  if (digest(ts.sys.readFile(document.file) ?? '') !== document.hash) { invalid = true; throw Error('Document changed'); }
  for (const item of files.values()) {
    if (digest(ts.sys.readFile(item.file) ?? '') !== item.hash) { invalid = true; throw Error('Dependency changed'); }
  }
}
function buildService() {
  const configFile = ts.findConfigFile(path.dirname(document.file), ts.sys.fileExists, 'tsconfig.json') ??
    ts.findConfigFile(path.dirname(document.file), ts.sys.fileExists, 'jsconfig.json');
  let options = {allowJs:true, checkJs:true, noEmit:true, strict:true, jsx:ts.JsxEmit.ReactJSX,
    target:ts.ScriptTarget.ES2022, module:ts.ModuleKind.ESNext, moduleResolution:ts.ModuleResolutionKind.Node10};
  if (configFile && path.relative(root, configFile).split(path.sep)[0] !== '..') {
    const config = ts.parseConfigFileTextToJson(configFile, snapshot(configFile));
    if (config.error) throw Error('Invalid project config');
    const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, path.dirname(configFile));
    options = {...options, ...parsed.options, noEmit:true};
  }
  // Only the opened root plus its imported dependencies. Compiler plugins are never loaded.
  delete options.plugins;
  const host = {
    getScriptFileNames: () => [document.file], getScriptVersion: () => '1',
    getScriptSnapshot: file => {const text = snapshot(file); return text === undefined ? undefined : ts.ScriptSnapshot.fromString(text);},
    getCurrentDirectory: () => root, getCompilationSettings: () => options,
    getDefaultLibFileName: value => ts.getDefaultLibFilePath(value),
    fileExists: ts.sys.fileExists, readFile: snapshot, readDirectory: ts.sys.readDirectory,
    directoryExists: ts.sys.directoryExists, getDirectories: ts.sys.getDirectories,
    useCaseSensitiveFileNames: () => ts.sys.useCaseSensitiveFileNames,
  };
  service = ts.createLanguageService(host);
}
function source(file) {
  return ts.createSourceFile(file, snapshot(file) ?? '', ts.ScriptTarget.Latest, false);
}
function range(file, span) {
  const code = source(file), a = code.getLineAndCharacterOfPosition(span.start), b = code.getLineAndCharacterOfPosition(span.start + span.length);
  return {start:{line:a.line,character:a.character}, end:{line:b.line,character:b.character}};
}
function point(position) {
  if (!Number.isInteger(position.line) || !Number.isInteger(position.character) || position.line < 0 || position.character < 0) throw Error('Invalid position');
  return source(document.file).getPositionOfLineAndCharacter(position.line, position.character);
}
function requireDocument(params) {
  if (!initialized || closing || params.textDocument?.uri !== document?.uri) throw Error('Document mismatch');
  fresh();
}
function handle(method, params = {}) {
  if (method === 'initialize') {
    if (root) throw Error('Already initialized');
    root = localUri(params.rootUri); noLinks(root);
    return {capabilities:{positionEncoding:'utf-16', textDocumentSync:1,
      diagnosticProvider:{interFileDependencies:true,workspaceDiagnostics:false}, definitionProvider:true,referencesProvider:true},
      serverInfo:{name:'Neo React immutable snapshot experiment',version:'0.1.0-ts5.9.3'}};
  }
  if (method === 'initialized') {initialized = true; return;}
  if (method === 'textDocument/didOpen') {
    const value = params.textDocument;
    if (!initialized || document || value?.version !== 1 || !['javascript','javascriptreact','typescript','typescriptreact'].includes(value.languageId) || typeof value.text !== 'string' || Buffer.byteLength(value.text) > 2*1024*1024) throw Error('Invalid immutable open');
    const file = scoped(value.uri);
    document = {file, uri:value.uri, text:value.text, hash:digest(value.text)};
    buildService(); fresh(); return;
  }
  if (method === 'textDocument/didChange') {invalid = true; throw Error('Immutable document changed');}
  if (method === 'textDocument/diagnostic') {
    requireDocument(params);
    const diagnostics = [...service.getSyntacticDiagnostics(document.file), ...service.getSemanticDiagnostics(document.file)];
    fresh();
    return {kind:'full',resultId:document.hash, items:diagnostics.filter(x => x.file && normalize(x.file.fileName) === normalize(document.file)).map(x => ({
      range:range(document.file,{start:x.start ?? 0,length:x.length ?? 0}),
      severity:x.category === ts.DiagnosticCategory.Error ? 1 : x.category === ts.DiagnosticCategory.Warning ? 2 : 3,
      code:x.code, source:'typescript', message:ts.flattenDiagnosticMessageText(x.messageText,'\n')}))};
  }
  if (method === 'textDocument/definition' || method === 'textDocument/references') {
    requireDocument(params);
    const offset = point(params.position);
    const values = method.endsWith('/definition') ? service.getDefinitionAtPosition(document.file,offset) : service.getReferencesAtPosition(document.file,offset);
    const result = (values ?? []).map(x => ({uri:pathToFileURL(x.fileName).href, range:range(x.fileName,x.textSpan)}));
    fresh(); return result;
  }
  if (method === 'textDocument/didClose') {if (params.textDocument?.uri !== document?.uri) throw Error('Document mismatch'); return;}
  if (method === 'shutdown') {closing = true; service?.dispose(); return null;}
  if (method === 'exit') {process.exitCode = closing ? 0 : 1; process.stdin.destroy(); return;}
  if (method === '$/cancelRequest') {invalid = true; return;}
  throw Error('Unsupported method');
}
function send(value) {
  const bytes = Buffer.from(JSON.stringify({jsonrpc:'2.0',...value}));
  if (bytes.length > MAX) throw Error('Oversize response');
  process.stdout.write(`Content-Length: ${bytes.length}\r\n\r\n`); process.stdout.write(bytes);
}
let buffer = Buffer.alloc(0), length;
process.stdin.on('data', chunk => {
  try {
    buffer = Buffer.concat([buffer,chunk]);
    while (true) {
      if (length === undefined) {
        const end = buffer.indexOf('\r\n\r\n');
        if (end < 0) {if(buffer.length>8192) throw Error('Oversize header'); break;}
        if (end > 8192) throw Error('Oversize header');
        const lengths = buffer.subarray(0,end).toString('ascii').split('\r\n').filter(x=>/^content-length:/i.test(x));
        if (lengths.length !== 1 || !/^content-length:\s*[0-9]+$/i.test(lengths[0])) throw Error('Invalid header');
        length = Number(lengths[0].split(':')[1]);
        if (length < 1 || length > MAX) throw Error('Oversize frame');
        buffer = buffer.subarray(end+4);
      }
      if (buffer.length < length) break;
      const message = JSON.parse(buffer.subarray(0,length).toString('utf8'));
      buffer = buffer.subarray(length); length = undefined;
      if (message.jsonrpc !== '2.0' || typeof message.method !== 'string') throw Error('Invalid RPC');
      try {
        trace(message.method,'start');
        const result = handle(message.method,message.params);
        trace(message.method,'complete');
        if (Object.hasOwn(message,'id')) send({id:message.id,result:result ?? null});
      } catch (error) {
        trace(message.method,'rejected',error.message);
        invalid = true;
        if (Object.hasOwn(message,'id')) send({id:message.id,error:{code:-32602,message:'Snapshot or request rejected.'}});
      }
    }
  } catch {process.exitCode = 1; process.stdin.destroy();}
});
