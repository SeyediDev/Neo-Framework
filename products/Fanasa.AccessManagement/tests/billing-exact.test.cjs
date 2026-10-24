'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const exact = require('../src/Fanasa.AccessManagement.Web/wwwroot/js/billing-exact.js');
test('financial JSON stays exact above JavaScript safe integer',()=>{
 const result=exact.parse('{"amount":9007199254740993,"quantity":0.125,"revision":9007199254740994,"id":"n"}');
 assert.equal(result.amount,'9007199254740993'); assert.equal(result.quantity,'0.125'); assert.equal(result.revision,'9007199254740994');
});
test('nested invoice line prices and credits remain strings',()=>{
 const result=exact.parse('{"invoices":[{"amount":12.30,"lines":[{"unitPrice":0.000001,"netAmount":12.30}]}],"outstanding":12.30}');
 assert.equal(result.invoices[0].lines[0].unitPrice,'0.000001');assert.equal(result.outstanding,'12.30');
});
test('unrelated strings cannot turn into JSON structure',()=>{
 const text='quantity \\"amount\\":999999999999999999999';
 assert.equal(exact.parse(JSON.stringify({reference:text,amount:1})).reference,text);
});
test('minor-unit arithmetic stays exact',()=>{
 assert.equal(exact.units('9007199254740993.25',2)-exact.units('0.10',2),900719925474099315n);
 assert.equal(exact.decimal(900719925474099315n,2),'9007199254740993.15');
});
test('unrepresentable minor units fail',()=>{
 assert.throws(()=>exact.units('1.001',2));assert.equal(exact.units('1.000',2),100n);
});
test('formatting keeps all digits',()=>{
 const shown=exact.format('9007199254740993.15');
 assert.match(shown,/۹٬۰۰۷٬۱۹۹٬۲۵۴٬۷۴۰٬۹۹۳/); assert.ok(shown.endsWith('٫۱۵'));
});
test('unexpected number input fails instead of accepting rounding',()=>{
 assert.throws(()=>exact.format(9007199254740993));assert.throws(()=>exact.units('1e10',0));
});
test('zero and negative exact differences format consistently',()=>{
 assert.equal(exact.decimal(-1n,2),'-0.01');assert.equal(exact.format('0'),'۰');
});
test('bounded parser rejects malformed responses',()=>{
 assert.throws(()=>exact.parse('{"amount":}'));assert.throws(()=>exact.parse(' '.repeat(8*1024*1024+1)));
});
