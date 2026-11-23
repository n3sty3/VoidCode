/* VoidShell built-in commands - all 14 in one script.
 *
 * this file replaces the 14 console/commands/*.js files. They were one script tag
 * each, which made the fallback shell 20 requests on every start; the shell is lazy now, so the
 * registry and its commands arrive as one payload. Every block below is the original file verbatim
 * (re-indented by two spaces inside the wrapper, which is what makes ls.js's sizeLabel local). */
(function () {
  // ---- cat.js
  window.VoidShell.register({
    name: 'cat',
    description: 'print a file',
    run: async function (args, ctx) {
      if (!args.length) { ctx.err('void: cat: missing file operand'); return 1; }
      for (var i = 0; i < args.length; i += 1) {
        var res = await ctx.fs.read(ctx.resolve(args[i]));
        var text = res && res.content != null ? res.content : null;
        if (text == null) { ctx.err('void: cannot read: ' + args[i]); return 1; }
        ctx.print(text);
      }
    }
  });

  // ---- cd.js
  window.VoidShell.register({
    name: 'cd',
    description: 'change directory',
    run: async function (args, ctx) {
      var target = ctx.resolve(args[0] || ctx.root || ctx.cwd);
      var listing = await ctx.fs.list(target);
      if (!listing || listing.error) { ctx.err('void: no such directory: ' + (args[0] || target)); return 1; }
      ctx.setCwd(target);
    }
  });

  // ---- clear.js
  window.VoidShell.register({
    name: 'clear',
    description: 'clear the console',
    run: function (args, ctx) { ctx.clear(); }
  });

  // ---- echo.js
  window.VoidShell.register({
    name: 'echo',
    description: 'print the arguments',
    run: function (args, ctx) { ctx.print(args.join(' ')); }
  });

  // ---- exit.js
  window.VoidShell.register({
    name: 'exit',
    description: 'close the console',
    run: function (args, ctx) {
      ctx.print('closing console', 'dim');
      ctx.close();
    }
  });

  // ---- help.js
  window.VoidShell.register({
    name: 'help',
    description: 'list available commands',
    run: function (args, ctx) {
      var rows = window.VoidShell.list();
      var width = 0;
      rows.forEach(function (c) { if (c.name.length > width) width = c.name.length; });
      ctx.print('void shell - built-in commands', 'dim');
      rows.forEach(function (c) {
        var pad = c.name;
        while (pad.length < width + 4) pad += ' ';
        ctx.print('  ' + pad + (c.description || ''));
      });
      ctx.print('');
      ctx.print('anything else runs as a program (git, npm, node, ...)', 'dim');
    }
  });

  // ---- history.js
  window.VoidShell.register({
    name: 'history',
    description: 'show command history',
    run: function (args, ctx) {
      if (!ctx.history.length) { ctx.print('(no history)', 'dim'); return 0; }
      ctx.history.slice().reverse().forEach(function (line, index) {
        var n = String(index + 1);
        while (n.length < 3) n = ' ' + n;
        ctx.print(n + '  ' + line, 'dim');
      });
    }
  });

  // ---- ls.js
  function sizeLabel(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / 1048576).toFixed(1) + ' MB';
  }

  window.VoidShell.register({
    name: 'ls',
    description: 'list a directory',
    run: async function (args, ctx) {
      var target = ctx.resolve(args[0] || ctx.cwd);
      var listing = await ctx.fs.list(target);
      if (!listing || listing.error) { ctx.err('void: cannot list: ' + (args[0] || target)); return 1; }
      var entries = (listing.entries || []).slice().sort(function (a, b) {
        if (a.isDir !== b.isDir) return a.isDir ? -1 : 1;
        return a.name.toLowerCase() < b.name.toLowerCase() ? -1 : 1;
      });
      if (!entries.length) { ctx.print('(empty)', 'dim'); return 0; }
      entries.forEach(function (e) {
        if (e.isDir) ctx.print(e.name + '\\', 'dir');
        else ctx.print(e.name + '  ' + sizeLabel(e.size || 0), 'dim');
      });
    }
  });

  // ---- mkdir.js
  window.VoidShell.register({
    name: 'mkdir',
    description: 'create a directory',
    run: async function (args, ctx) {
      if (!args.length) { ctx.err('void: mkdir: missing operand'); return 1; }
      for (var i = 0; i < args.length; i += 1) {
        var res = await ctx.fs.mkdir(ctx.resolve(args[i]));
        if (res && res.error) { ctx.err('void: ' + res.error); return 1; }
        ctx.print('created ' + args[i], 'dim');
      }
    }
  });

  // ---- open.js
  window.VoidShell.register({
    name: 'open',
    description: 'open a file in the editor',
    run: function (args, ctx) {
      if (!args.length) { ctx.err('void: open: missing file operand'); return 1; }
      var target = ctx.resolve(args[0]);
      ctx.openFile(target);
      ctx.print('opened ' + ctx.display(target), 'dim');
    }
  });

  // ---- pwd.js
  window.VoidShell.register({
    name: 'pwd',
    description: 'print the working directory',
    run: function (args, ctx) { ctx.print(ctx.cwd); }
  });

  // ---- rm.js
  window.VoidShell.register({
    name: 'rm',
    description: 'delete a file or directory',
    run: async function (args, ctx) {
      if (!args.length) { ctx.err('void: rm: missing operand'); return 1; }
      for (var i = 0; i < args.length; i += 1) {
        var target = ctx.resolve(args[i]);
        var res = await ctx.fs.remove(target);
        if (res && res.error) { ctx.err('void: ' + res.error); return 1; }
        ctx.print('removed ' + ctx.display(target), 'dim');
      }
    }
  });

  // ---- theme.js
  window.VoidShell.register({
    name: 'theme',
    description: 'switch theme: ' + 'harbor | obsidian | crimson | emerald | nebula',
    run: function (args, ctx) {
      if (!args.length) { ctx.print('themes: ' + ctx.themes.join(', '), 'dim'); return 0; }
      var name = String(args[0]).toLowerCase();
      if (ctx.themes.indexOf(name) === -1) { ctx.err('void: unknown theme: ' + name); return 1; }
      if (!ctx.setTheme(name)) { ctx.err('void: could not switch theme'); return 1; }
      ctx.print('theme set to ' + name, 'ok');
    }
  });

  // ---- touch.js
  window.VoidShell.register({
    name: 'touch',
    description: 'create an empty file',
    run: async function (args, ctx) {
      if (!args.length) { ctx.err('void: touch: missing operand'); return 1; }
      for (var i = 0; i < args.length; i += 1) {
        var res = await ctx.fs.touch(ctx.resolve(args[i]));
        if (res && res.error) { ctx.err('void: ' + res.error); return 1; }
        ctx.print('created ' + args[i], 'dim');
      }
    }
  });
})();
