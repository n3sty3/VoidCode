// Headless check of VoidShell: registry, parser, and the command-not-found path.
// Run with: node tests/console-smoke.js
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, '..', 'wwwroot');
const ctx = { window: {}, document: { readyState: 'loading', addEventListener() { }, getElementById() { return null; } } };
ctx.window.window = ctx.window;
ctx.window.document = ctx.document;
ctx.window.VoidBridge = { request: async () => ({}), send() { } };
ctx.window.addEventListener = () => { };
vm.createContext(ctx);

function load(rel) {
  const code = fs.readFileSync(path.join(root, rel), 'utf8');
  vm.runInContext(code, ctx, { filename: rel });
}

load('console/registry.js');
// The 14 command files were merged into one console/commands.js.
load('console/commands.js');
load('console/parser.js');

const S = ctx.window.VoidShell;
const P = ctx.window.VoidParse;

let failures = 0;
function check(label, condition, detail) {
  if (condition) { console.log('  ok   ' + label); return; }
  failures += 1;
  console.log('  FAIL ' + label + (detail ? ' -> ' + detail : ''));
}

console.log('VoidShell smoke test');

const names = S.list().map(c => c.name);
const expected = ['cat', 'cd', 'clear', 'echo', 'exit', 'help', 'history', 'ls', 'mkdir', 'open', 'pwd', 'rm', 'theme', 'touch'];
check('14 commands registered', names.length === 14, names.join(','));
expected.forEach(n => check('command present: ' + n, names.includes(n)));
check('every command has a description', S.list().every(c => c.description && c.description.length > 2));
check('every command has run()', S.list().every(c => typeof c.run === 'function'));
check('unknown name is not a builtin', S.get('asd') === null);

const p1 = P.parse('ls "my dir" | cat > out.txt && pwd');
check('two && chains parsed', p1.chains.length === 2, JSON.stringify(p1.chains && p1.chains.length));
check('first chain has two stages', p1.chains[0].stages.length === 2);
check('quoted arg kept whole', p1.chains[0].stages[0].argv[1] === 'my dir', JSON.stringify(p1.chains[0].stages[0].argv));
check('redirect captured on second stage', p1.chains[0].stages[1].redirect === 'out.txt', JSON.stringify(p1.chains[0].stages[1]));
check('second chain is pwd', p1.chains[1].stages[0].argv[0] === 'pwd');

const p2 = P.parse("echo 'a b' c");
check('single quotes respected', JSON.stringify(p2.chains[0].stages[0].argv) === JSON.stringify(['echo', 'a b', 'c']));

check('unterminated quote is an error', !!P.parse('echo "abc').error);
check('dangling redirect is an error', !!P.parse('ls >').error);

console.log(failures === 0 ? 'RESULT: PASS' : 'RESULT: ' + failures + ' FAILURE(S)');
process.exit(failures === 0 ? 0 : 1);