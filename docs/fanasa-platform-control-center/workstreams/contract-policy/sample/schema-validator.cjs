'use strict';

// A bounded, dependency-free validator for this supporting sample's schema subset.
// This is not a general JSON Schema implementation. References are local only.
const fs = require('node:fs');
const path = require('node:path');

const SCHEMA_DIRECTORY = path.resolve(__dirname, '../schemas');
const MAX_DEPTH = 64;
const MAX_NODES = 10000;
const origins = new WeakMap();
const own = (value, key) => Object.prototype.hasOwnProperty.call(value, key);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const keywords = new Set([
  '$schema', '$id', '$comment', 'title', 'description', 'default', 'examples',
  'deprecated', 'readOnly', 'writeOnly', '$defs', '$ref', 'type', 'properties',
  'required', 'additionalProperties', 'items', 'contains', 'minContains', 'maxContains',
  'minItems', 'maxItems', 'uniqueItems',
  'minLength', 'maxLength', 'pattern', 'enum', 'const', 'minimum', 'maximum',
  'exclusiveMinimum', 'format', 'oneOf', 'allOf', 'if', 'then', 'else', 'not'
]);
const types = new Set(['null', 'boolean', 'object', 'array', 'number', 'integer', 'string']);

function localFilename(filename) {
  if (typeof filename !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._-]*\.json$/.test(filename) || filename.includes('..')) {
    throw new Error('Schema filename must be a local .json filename without directories.');
  }
  return filename;
}

function loadSchema(filename) {
  const name = localFilename(filename);
  const schema = JSON.parse(fs.readFileSync(path.join(SCHEMA_DIRECTORY, name), 'utf8'));
  if (typeof schema !== 'boolean' && !object(schema)) {
    throw new Error(`Schema ${name} must be an object or boolean.`);
  }
  if (object(schema)) origins.set(schema, name);
  return schema;
}

function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (object(value)) return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  return JSON.stringify(value);
}

function typeMatches(type, value) {
  if (type === 'null') return value === null;
  if (type === 'array') return Array.isArray(value);
  if (type === 'object') return object(value);
  if (type === 'number') return typeof value === 'number' && Number.isFinite(value);
  if (type === 'integer') return Number.isSafeInteger(value);
  return typeof value === type;
}

function dateTime(value) {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)) return false;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && new Date(timestamp).toISOString() === value;
}

function validateSchema(schema, value) {
  const errors = [];
  const documents = new Map();
  const references = new WeakMap();
  const checked = new WeakSet();
  const active = new WeakSet();
  let schemaNodes = 0;
  let valueNodes = 0;
  let evaluationNodes = 0;
  const origin = object(schema) && origins.get(schema);
  if (origin) documents.set(origin, schema);
  const fail = (location, message) => errors.push(`${location}: ${message}`);

  function resolve(reference, root) {
    if (typeof reference !== 'string') throw new Error('$ref must be a string.');
    const parts = reference.split('#');
    if (parts.length > 2) throw new Error('$ref must name a local schema or a local $defs pointer.');
    const filename = parts[0];
    const fragment = parts[1];
    if (filename) {
      localFilename(filename);
      if (!documents.has(filename)) documents.set(filename, loadSchema(filename));
      root = documents.get(filename);
    }
    if (fragment === undefined) {
      if (!filename) throw new Error('$ref must not be empty.');
      return { schema: root, root };
    }
    if (!fragment.startsWith('/$defs/') || fragment.length === 7 || fragment.includes('%') || /~(?![01])/.test(fragment)) {
      throw new Error('$ref fragments must be local #/$defs/... JSON pointers.');
    }
    let target = root;
    for (const token of fragment.slice(1).split('/')) {
      const key = token.replace(/~1/g, '/').replace(/~0/g, '~');
      if (!object(target) || !own(target, key)) throw new Error(`Unresolved local $ref ${reference}.`);
      target = target[key];
    }
    return { schema: target, root };
  }

  // Check all schema nodes, including unused definitions and conditional branches.
  function check(node, root, location, depth) {
    if (schemaNodes > MAX_NODES) return;
    if (depth > MAX_DEPTH || ++schemaNodes > MAX_NODES) {
      fail(location, 'schema exceeds the sample validator limits.');
      return;
    }
    if (typeof node === 'boolean') return;
    if (!object(node)) { fail(location, 'schema must be an object or boolean.'); return; }
    if (active.has(node)) { fail(location, 'recursive schemas are unsupported by this sample.'); return; }
    if (checked.has(node)) return;
    active.add(node);
    for (const key of Object.keys(node)) {
      if (!keywords.has(key)) fail(location, `unsupported schema keyword ${JSON.stringify(key)}.`);
    }
    const requireRule = (key, valid, message) => {
      if (own(node, key) && !valid(node[key])) fail(location, `${key} ${message}.`);
    };
    requireRule('type', rule => typeof rule === 'string' ? types.has(rule) : Array.isArray(rule) && rule.length > 0 && rule.every(type => types.has(type)) && new Set(rule).size === rule.length, 'must contain supported, distinct JSON types');
    requireRule('required', rule => Array.isArray(rule) && rule.every(item => typeof item === 'string') && new Set(rule).size === rule.length, 'must be an array of distinct property names');
    requireRule('additionalProperties', rule => typeof rule === 'boolean', 'must be boolean in this sample');
    requireRule('uniqueItems', rule => typeof rule === 'boolean', 'must be boolean');
    for (const key of ['minItems', 'maxItems', 'minContains', 'maxContains', 'minLength', 'maxLength']) {
      requireRule(key, rule => Number.isSafeInteger(rule) && rule >= 0, 'must be a nonnegative safe integer');
    }
    for (const key of ['minimum', 'maximum', 'exclusiveMinimum']) {
      requireRule(key, rule => typeof rule === 'number' && Number.isFinite(rule) && (!Number.isInteger(rule) || Number.isSafeInteger(rule)), 'must be a finite JSON number with safe integer precision');
    }
    requireRule('enum', rule => Array.isArray(rule) && rule.length > 0, 'must be a nonempty array');
    if (Array.isArray(node.enum)) {
      const previousErrors = errors.length;
      node.enum.forEach((candidate, index) => checkValue(candidate, `${location}/enum/${index}`, 0, new WeakSet()));
      if (errors.length === previousErrors && valueNodes <= MAX_NODES && new Set(node.enum.map(canonical)).size !== node.enum.length) fail(location, 'enum values must be distinct.');
    }
    if (own(node, 'const')) checkValue(node.const, `${location}/const`, 0, new WeakSet());
    requireRule('format', rule => rule === 'date-time', 'only supports date-time');
    if (own(node, 'pattern')) {
      if (typeof node.pattern !== 'string') fail(location, 'pattern must be a string.');
      else { try { new RegExp(node.pattern, 'u'); } catch { fail(location, 'pattern must be a valid Unicode regular expression.'); } }
    }
    for (const key of ['properties', '$defs']) {
      if (!own(node, key)) continue;
      if (!object(node[key])) fail(location, `${key} must be an object of schemas.`);
      else for (const [name, child] of Object.entries(node[key])) {
        check(child, root, `${location}/${key}/${name}`, depth + 1);
        if (schemaNodes > MAX_NODES) break;
      }
    }
    for (const key of ['oneOf', 'allOf']) {
      if (!own(node, key)) continue;
      if (!Array.isArray(node[key]) || node[key].length === 0) fail(location, `${key} must be a nonempty array of schemas.`);
      else for (let index = 0; index < node[key].length; index++) {
        check(node[key][index], root, `${location}/${key}/${index}`, depth + 1);
        if (schemaNodes > MAX_NODES) break;
      }
    }
    for (const key of ['items', 'contains', 'if', 'then', 'else', 'not']) {
      if (own(node, key)) check(node[key], root, `${location}/${key}`, depth + 1);
    }
    if (own(node, '$ref')) {
      try {
        const target = resolve(node.$ref, root);
        references.set(node, target);
        check(target.schema, target.root, `${location}/$ref(${node.$ref})`, depth + 1);
      } catch (error) { fail(location, error.message); }
    }
    active.delete(node);
    checked.add(node);
  }

  function checkValue(item, location, depth, ancestors) {
    if (valueNodes > MAX_NODES) return;
    if (depth > MAX_DEPTH || ++valueNodes > MAX_NODES) { fail(location, 'value exceeds the sample validator limits.'); return; }
    if (item === null || typeof item === 'string' || typeof item === 'boolean') return;
    if (typeof item === 'number') {
      if (!Number.isFinite(item) || (Number.isInteger(item) && !Number.isSafeInteger(item))) fail(location, 'numbers must be finite and integers must be safe.');
      return;
    }
    if (typeof item !== 'object' || (!Array.isArray(item) && Object.getPrototypeOf(item) !== Object.prototype && Object.getPrototypeOf(item) !== null)) {
      fail(location, 'value must contain only JSON values.'); return;
    }
    if (ancestors.has(item)) { fail(location, 'cyclic values are unsupported.'); return; }
    ancestors.add(item);
    if (Array.isArray(item)) {
      for (let index = 0; index < item.length; index++) {
        checkValue(item[index], `${location}[${index}]`, depth + 1, ancestors);
        if (valueNodes > MAX_NODES) break;
      }
    } else {
      for (const [key, child] of Object.entries(item)) {
        checkValue(child, `${location}[${JSON.stringify(key)}]`, depth + 1, ancestors);
        if (valueNodes > MAX_NODES) break;
      }
    }
    ancestors.delete(item);
  }

  function evaluate(node, item, location, output, depth) {
    const add = message => output.push(`${location}: ${message}`);
    if (depth > MAX_DEPTH || ++evaluationNodes > MAX_NODES) { add('schema evaluation exceeds the sample validator limits.'); return; }
    if (node === true) return;
    if (node === false) { add('value is forbidden by the schema.'); return; }
    const matches = child => {
      const branchErrors = [];
      evaluate(child, item, location, branchErrors, depth + 1);
      return branchErrors.length === 0;
    };
    if (own(node, '$ref')) evaluate(references.get(node).schema, item, location, output, depth + 1);
    if (own(node, 'type')) {
      const allowed = Array.isArray(node.type) ? node.type : [node.type];
      if (!allowed.some(type => typeMatches(type, item))) add(`must have type ${allowed.join(' or ')}.`);
    }
    if (own(node, 'const') && canonical(item) !== canonical(node.const)) add('must equal const.');
    if (own(node, 'enum') && !node.enum.some(candidate => canonical(item) === canonical(candidate))) add('must match an enum value.');
    if (typeof item === 'string') {
      const length = [...item].length;
      if (own(node, 'minLength') && length < node.minLength) add(`must have at least ${node.minLength} characters.`);
      if (own(node, 'maxLength') && length > node.maxLength) add(`must have at most ${node.maxLength} characters.`);
      if (own(node, 'pattern') && !new RegExp(node.pattern, 'u').test(item)) add(`must match pattern ${JSON.stringify(node.pattern)}.`);
      if (node.format === 'date-time' && !dateTime(item)) add('must be a real canonical UTC date-time with milliseconds.');
    }
    if (typeof item === 'number') {
      if (own(node, 'minimum') && item < node.minimum) add(`must be at least ${node.minimum}.`);
      if (own(node, 'maximum') && item > node.maximum) add(`must be at most ${node.maximum}.`);
      if (own(node, 'exclusiveMinimum') && item <= node.exclusiveMinimum) add(`must be greater than ${node.exclusiveMinimum}.`);
    }
    if (Array.isArray(item)) {
      if (own(node, 'minItems') && item.length < node.minItems) add(`must have at least ${node.minItems} items.`);
      if (own(node, 'maxItems') && item.length > node.maxItems) add(`must have at most ${node.maxItems} items.`);
      if (node.uniqueItems && new Set(item.map(canonical)).size !== item.length) add('must contain unique items.');
      if (own(node, 'items')) item.forEach((child, index) => evaluate(node.items, child, `${location}[${index}]`, output, depth + 1));
      if (own(node, 'contains')) {
        let count = 0;
        for (let index = 0; index < item.length; index++) {
          const itemErrors = [];
          evaluate(node.contains, item[index], `${location}[${index}]`, itemErrors, depth + 1);
          if (itemErrors.length === 0) count++;
          if (evaluationNodes > MAX_NODES) break;
        }
        const minimum = own(node, 'minContains') ? node.minContains : 1;
        if (count < minimum) add(`must contain at least ${minimum} matching items; matched ${count}.`);
        if (own(node, 'maxContains') && count > node.maxContains) add(`must contain at most ${node.maxContains} matching items; matched ${count}.`);
      }
    }
    if (object(item)) {
      for (const key of node.required || []) { if (!own(item, key)) add(`missing required property ${JSON.stringify(key)}.`); }
      const properties = node.properties || {};
      for (const [key, child] of Object.entries(item)) {
        if (own(properties, key)) evaluate(properties[key], child, `${location}[${JSON.stringify(key)}]`, output, depth + 1);
        else if (node.additionalProperties === false) add(`unknown property ${JSON.stringify(key)}.`);
      }
    }
    if (node.allOf) node.allOf.forEach(child => evaluate(child, item, location, output, depth + 1));
    if (node.oneOf) {
      const count = node.oneOf.filter(matches).length;
      if (count !== 1) add(`must match exactly one oneOf branch; matched ${count}.`);
    }
    if (own(node, 'not') && matches(node.not)) add('must not match the not schema.');
    if (own(node, 'if')) {
      const branch = matches(node.if) ? 'then' : 'else';
      if (own(node, branch)) evaluate(node[branch], item, location, output, depth + 1);
    }
  }

  check(schema, schema, '$schema', 0);
  if (errors.length) return errors;
  checkValue(value, '$', 0, new WeakSet());
  if (errors.length) return errors;
  evaluate(schema, value, '$', errors, 0);
  if (evaluationNodes > MAX_NODES) fail('$', 'schema evaluation exceeds the sample validator limits.');
  return errors;
}

module.exports = { validateSchema, loadSchema };
