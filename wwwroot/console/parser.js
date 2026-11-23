/* VoidShell parser.
 * Handles quoted arguments, `&&` chains, `|` pipelines and `>` redirect.
 * Deliberately small: no globbing, no variables, no substitution. */
(function (global) {
  'use strict';

  function tokenize(input) {
    const tokens = [];
    let current = '';
    let quote = null;
    let has = false;

    for (let i = 0; i < input.length; i += 1) {
      const ch = input[i];

      if (quote) {
        if (ch === quote) { quote = null; continue; }
        if (ch === '\\' && quote === '"' && i + 1 < input.length) { current += input[i + 1]; i += 1; continue; }
        current += ch;
        continue;
      }

      if (ch === '"' || ch === "'") { quote = ch; has = true; continue; }

      if (ch === '&' && input[i + 1] === '&') {
        if (has) { tokens.push({ type: 'word', value: current }); current = ''; has = false; }
        tokens.push({ type: 'op', value: '&&' }); i += 1; continue;
      }

      if (ch === '|') {
        if (has) { tokens.push({ type: 'word', value: current }); current = ''; has = false; }
        tokens.push({ type: 'op', value: '|' }); continue;
      }

      if (ch === '>') {
        if (has) { tokens.push({ type: 'word', value: current }); current = ''; has = false; }
        tokens.push({ type: 'op', value: '>' }); continue;
      }

      if (ch === ' ' || ch === '\t') {
        if (has) { tokens.push({ type: 'word', value: current }); current = ''; has = false; }
        continue;
      }

      current += ch;
      has = true;
    }

    if (quote) return { error: 'unterminated quote' };
    if (has) tokens.push({ type: 'word', value: current });
    return { tokens: tokens };
  }

  /**
   * parse('ls "my dir" | cat > out.txt && pwd')
   * -> { chains: [ { stages: [ {argv:[...], redirect:'out.txt'|null} ] } ] }
   */
  function parse(input) {
    const tok = tokenize(String(input || ''));
    if (tok.error) return { error: 'void: ' + tok.error };

    const chains = [];
    let stages = [];
    let stage = { argv: [], redirect: null };
    let expectRedirect = false;

    function closeStage() {
      if (stage.redirect !== null && stage.argv.length === 0) {
        // `>` with no command is meaningless
      }
      if (stage.argv.length > 0 || stage.redirect !== null) stages.push(stage);
      stage = { argv: [], redirect: null };
    }

    function closeChain() {
      closeStage();
      if (stages.length > 0) chains.push({ stages: stages });
      stages = [];
    }

    for (let i = 0; i < tok.tokens.length; i += 1) {
      const t = tok.tokens[i];

      if (t.type === 'op') {
        if (t.value === '|') { closeStage(); continue; }
        if (t.value === '&&') { closeChain(); continue; }
        if (t.value === '>') { expectRedirect = true; continue; }
      }

      if (expectRedirect) {
        stage.redirect = t.value;
        expectRedirect = false;
        continue;
      }

      stage.argv.push(t.value);
    }

    if (expectRedirect) return { error: 'void: syntax error: expected a file after >' };
    closeChain();
    return { chains: chains };
  }

  global.VoidParse = { parse: parse, tokenize: tokenize };
})(window);