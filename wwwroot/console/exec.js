/* VoidShell executor: stages, pipelines, &&, redirect. */
(function () {
  'use strict';
  const S = window.VoidShell;
  const C = S.core;

  async function runStage(stage, stdin, piped) {
    const argv = stage.argv;
    if (!argv.length) return { code: 0, stdout: '' };

    const command = S.get(String(argv[0]).toLowerCase());
    const renderLive = !piped && !stage.redirect;

    if (command) {
      const ctx = C.makeCtx(argv, { stdin: stdin, render: renderLive, piped: piped });
      let code = 0;
      try {
        const result = await command.run(ctx.args, ctx);
        if (typeof result === 'number') code = result;
      } catch (error) {
        C.appendLine('void: ' + (error && error.message ? error.message : 'command failed'), 'err');
        code = 1;
      }
      return { code: code, stdout: ctx.stdout() };
    }

    const res = await C.spawn(argv[0], argv.slice(1), C.state.cwd, stdin || null);
    if (res == null || res.error) {
      // The console bridge already surfaced the timeout to the user; stop the pipeline here.
      return { code: 1, stdout: '' };
    }
    if (res.exitCode === 127) {
      if (renderLive) C.appendLine('void: command not found: ' + argv[0], 'err');
      return { code: 127, stdout: '' };
    }
    if (renderLive) {
      if (res.stdout) C.appendBlock(res.stdout, null);
      if (res.stderr) C.appendBlock(res.stderr, 'err');
    }
    return { code: res.exitCode || 0, stdout: res.stdout || '' };
  }

  async function execute(line) {
    const raw = String(line == null ? '' : line);
    C.pushHistory(raw.trim());
    if (!raw.trim()) return;

    const parsed = window.VoidParse.parse(raw);
    if (parsed.error) { C.appendLine(parsed.error, 'err'); return; }

    let stdin = '';
    for (let c = 0; c < parsed.chains.length; c += 1) {
      const stages = parsed.chains[c].stages;
      for (let i = 0; i < stages.length; i += 1) {
        const stage = stages[i];
        const piped = i > 0 || i < stages.length - 1;
        if (i === 0 && stage.argv.length === 0) continue;

        const res = await runStage(stage, stdin, piped);
        stdin = res.stdout;

        if (stage.redirect) {
          const target = C.resolvePath(stage.redirect);
          const written = await C.fs.write(target, res.stdout);
          if (written && written.error) { C.appendLine('void: ' + written.error, 'err'); return; }
          C.appendLine('void: wrote ' + C.displayPath(target), 'dim');
        }

        if (res.code !== 0) return;
      }
    }
  }

  S.execute = execute;
})();