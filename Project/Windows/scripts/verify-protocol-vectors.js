// Independent verification of the Win<->Mac v1 protocol test vectors
// documented in main:开发文档/通信协议.md section 8.
// Nothing here is shared with the macOS or Windows implementation.

const crypto = require('crypto');

let failures = 0;
function check(name, actual, expected) {
  const ok = actual === expected;
  if (!ok) failures++;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`);
  if (!ok) {
    console.log(`      expected: ${expected}`);
    console.log(`      actual  : ${actual}`);
  }
}

const hex = (buf) => Buffer.from(buf).toString('hex');

// ---------------------------------------------------------------- offer encoding
// 3.5: u16 version | u8 role | u32 len+name | u32 len+idKey | u32 len+eph | u32 len+nonce | u32 len+code
function encodeOffer(o) {
  const parts = [];
  const u16 = Buffer.alloc(2); u16.writeUInt16BE(o.version);
  const u32 = (n) => { const b = Buffer.alloc(4); b.writeUInt32BE(n); return b; };
  parts.push(u16, Buffer.from([o.role]));
  const fields = [Buffer.from(o.deviceName, 'utf8'), o.identityPublicKeyDER, o.ephemeralPublicKey, o.nonce, Buffer.from(o.sessionCode, 'ascii')];
  for (const f of fields) { parts.push(u32(f.length), f); }
  return Buffer.concat(parts);
}

const initiatorOffer = {
  version: 1, role: 1, deviceName: 'Mac-A',
  identityPublicKeyDER: Buffer.alloc(8, 0xAA),
  ephemeralPublicKey: Buffer.alloc(32, 0x11),
  nonce: Buffer.alloc(32, 0x22),
  sessionCode: '123456',
};
const responderOffer = {
  version: 1, role: 2, deviceName: 'Win-B',
  identityPublicKeyDER: Buffer.alloc(8, 0xBB),
  ephemeralPublicKey: Buffer.alloc(32, 0x33),
  nonce: Buffer.alloc(32, 0x44),
  sessionCode: '123456',
};

const encInitiator = encodeOffer(initiatorOffer);
const encResponder = encodeOffer(responderOffer);

check('8.1 offer(initiator) encoding',
  hex(encInitiator),
  '000101000000054d61632d4100000008aaaaaaaaaaaaaaaa00000020111111111111111111111111111111111111111111111111111111111111111100000020222222222222222222222222222222222222222222222222222222222222222200000006313233343536');

check('8.1 offer(responder) encoding',
  hex(encResponder),
  '0001020000000557696e2d4200000008bbbbbbbbbbbbbbbb00000020333333333333333333333333333333333333333333333333333333333333333300000020444444444444444444444444444444444444444444444444444444444444444400000006313233343536');

// ---------------------------------------------------------------- transcript / digest
const magic = Buffer.concat([Buffer.from('FEIGE-HANDSHAKE-V1', 'ascii'), Buffer.from([0x00])]);
const transcript = Buffer.concat([magic, encInitiator, encResponder]);
const digest = crypto.createHash('sha256').update(transcript).digest();

check('8.1 transcript digest', hex(digest), '779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273');

// ---------------------------------------------------------------- SAS
const n = digest.readUInt32BE(0);
const sas = String(n % 1000000).padStart(6, '0');
check('8.1 SAS', sas, '472138');

// ---------------------------------------------------------------- nonce prefixes
check('8.1 nonce prefix (initiator)', hex(Buffer.concat([digest.subarray(0, 4), Buffer.from('INIT', 'ascii')])), '779855ca494e4954');
check('8.1 nonce prefix (responder)', hex(Buffer.concat([digest.subarray(0, 4), Buffer.from('RESP', 'ascii')])), '779855ca52455350');

// ---------------------------------------------------------------- fingerprint
function fingerprint(der) {
  const h = crypto.createHash('sha256').update(der).digest('hex').toUpperCase();
  return h.match(/.{1,4}/g).join(' ');
}
check('8.1 fingerprint(0xAA x8)',
  fingerprint(Buffer.alloc(8, 0xAA)),
  '6893 A08C 9C4C C97C 2279 9A6A A7AF 6F8B 8BC2 2D54 1A5E 4253 B180 BBC9 74C9 D497');
check('8.1 fingerprint(0xBB x8)',
  fingerprint(Buffer.alloc(8, 0xBB)),
  '8C7D 2C6F 7DEF 9A50 D4AD 3023 702D F661 BE86 637F 2854 EFF9 4DE6 0367 6A81 3341');

// ---------------------------------------------------------------- AES-256-GCM record
// 8.2: key=0x11 x32, nonce=prefix||seq, header=type|seq|ctLen as AAD, tag 16
const key = Buffer.alloc(32, 0x11);
const nonce = Buffer.concat([Buffer.from('779855ca494e4954', 'hex'), Buffer.alloc(8, 0)]);
const header = Buffer.concat([Buffer.from([0x01]), Buffer.alloc(8, 0), Buffer.from('00000021', 'hex')]);
const frame = Buffer.from('0000002e0100000000000000000000002102eff278f8692682ae1bec1a6c54adc0daf7e2e9d4e1b6bfbac728898115a6db64', 'hex');

check('8.2 header is 13 bytes', String(header.length), '13');
check('8.2 frame length field', String(frame.readUInt32BE(0)), String(frame.length - 4));

const ctLen = header.readUInt32BE(9);
const body = frame.subarray(4 + 13);
check('8.2 ctLen matches body', String(ctLen), String(body.length));

const bodyCiphertext = body.subarray(0, body.length - 16);
const bodyTag = body.subarray(body.length - 16);
check('8.2 ciphertext', hex(bodyCiphertext), '02eff278f8692682ae1bec1a6c54adc0da');
check('8.2 tag', hex(bodyTag), 'f7e2e9d4e1b6bfbac728898115a6db64');

try {
  const decipher = crypto.createDecipheriv('aes-256-gcm', key, nonce);
  decipher.setAAD(header);
  decipher.setAuthTag(bodyTag);
  const plaintext = Buffer.concat([decipher.update(bodyCiphertext), decipher.final()]);
  check('8.2 decrypts to "feige-record-test"', plaintext.toString('utf8'), 'feige-record-test');
} catch (e) {
  failures++;
  console.log(`FAIL  8.2 decrypt threw: ${e.message}`);
}

// negative control: tampered header must not authenticate
try {
  const badHeader = Buffer.from(header);
  badHeader[0] = 0x02; // flip type
  const d = crypto.createDecipheriv('aes-256-gcm', key, nonce);
  d.setAAD(badHeader);
  d.setAuthTag(bodyTag);
  Buffer.concat([d.update(bodyCiphertext), d.final()]);
  failures++;
  console.log('FAIL  tampered header was accepted');
} catch (e) {
  console.log('PASS  tampered header rejected (AAD is authenticated)');
}

// ---------------------------------------------------------------- multi-record sequence
// 8.3: same fixed key/prefix, sequence 0..2 with three different types, including empty finish.
function sealRecord(type, sequence, plaintext) {
  const h = Buffer.alloc(13);
  h[0] = type;
  h.writeBigUInt64BE(BigInt(sequence), 1);
  h.writeUInt32BE(plaintext.length + 16, 9);
  const n = Buffer.concat([Buffer.from('779855ca494e4954', 'hex'), h.subarray(1, 9)]);
  const c = crypto.createCipheriv('aes-256-gcm', Buffer.alloc(32, 0x11), n);
  c.setAAD(h);
  const body = Buffer.concat([c.update(plaintext), c.final(), c.getAuthTag()]);
  const framed = Buffer.concat([h, body]);
  const out = Buffer.alloc(4 + framed.length);
  out.writeUInt32BE(framed.length, 0);
  framed.copy(out, 4);
  return out;
}

const seqChunks = {
  manifest: sealRecord(1, 0, Buffer.from('feige-record-test', 'utf8')),
  chunk: sealRecord(3, 1, Buffer.from(Array.from({ length: 32 }, (_, i) => i))),
  finish: sealRecord(7, 2, Buffer.alloc(0)),
};

check('8.3 seq0 manifest frame',
  hex(seqChunks.manifest),
  '0000002e0100000000000000000000002102eff278f8692682ae1bec1a6c54adc0daf7e2e9d4e1b6bfbac728898115a6db64');
check('8.3 seq1 chunk frame',
  hex(seqChunks.chunk),
  '0000003d03000000000000000100000030d28326309f5a2dc13c837b11a21cdf3ea86263a9c58cc4f0467f4db33f72294547b6443017390c042da902933d1e4f52');
check('8.3 seq2 finish frame (empty payload)',
  hex(seqChunks.finish),
  '0000001d07000000000000000200000010bcc19843d8716dbafb740136ca263574');

// Reassembling the three frames as one byte stream must yield three records in order.
function openRecord(frame) {
  const len = frame.readUInt32BE(0);
  const h = frame.subarray(4, 17);
  const body = frame.subarray(17, 4 + len);
  const d = crypto.createDecipheriv('aes-256-gcm', Buffer.alloc(32, 0x11), Buffer.concat([Buffer.from('779855ca494e4954', 'hex'), h.subarray(1, 9)]));
  d.setAAD(h);
  d.setAuthTag(body.subarray(body.length - 16));
  return {
    type: h[0],
    sequence: Number(h.readBigUInt64BE(1)),
    plaintext: Buffer.concat([d.update(body.subarray(0, body.length - 16)), d.final()]),
  };
}

const streamed = [seqChunks.manifest, seqChunks.chunk, seqChunks.finish].map(openRecord);
check('8.3 decoded types', streamed.map((r) => r.type).join(','), '1,3,7');
check('8.3 decoded sequences', streamed.map((r) => r.sequence).join(','), '0,1,2');
check('8.3 decoded finish payload width', String(streamed[2].plaintext.length), '0');

// A header whose sequence is bumped but whose tag was kept must not authenticate.
try {
  const forged = Buffer.from(seqChunks.manifest);
  forged[8] = 0x01;
  openRecord(forged);
  failures++;
  console.log('FAIL  record with rewritten sequence was accepted');
} catch (e) {
  console.log('PASS  record with rewritten sequence rejected (sequence is in the AAD)');
}

console.log('');
console.log(failures === 0 ? 'ALL VECTORS VERIFIED' : `${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
