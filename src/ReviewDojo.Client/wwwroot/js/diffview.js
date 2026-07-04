window.dojoDiff = {
  _ref: null,

  render: function (elementId, diffText, mode, dotnetRef) {
    const target = document.getElementById(elementId);
    if (!target || typeof Diff2HtmlUI === 'undefined') return;
    if (dotnetRef) this._ref = dotnetRef;
    const ui = new Diff2HtmlUI(target, diffText, {
      drawFileList: false,
      matching: 'lines',
      outputFormat: mode === 'side' ? 'side-by-side' : 'line-by-line',
      colorScheme: 'dark',
      highlight: true
    });
    ui.draw();
    try { ui.highlightCode(); } catch (e) { /* highlight optional */ }
    this._attach(target);
  },

  _fileOf: function (row) {
    const w = row.closest('.d2h-file-wrapper');
    const n = w && w.querySelector('.d2h-file-name');
    return n ? n.textContent.trim() : '';
  },

  // new-file line number for this row, or null if the row has no new-side line
  _newLineOf: function (row) {
    // Unified (line-by-line): .line-num2 is the new-file number; empty on delete rows.
    const n2 = row.querySelector('.line-num2');
    if (n2) {
      const v = parseInt((n2.textContent || '').trim(), 10);
      return isNaN(v) ? null : v;
    }
    // Side-by-side: each row carries its own single line number.
    const side = row.querySelector('.d2h-code-side-linenumber');
    if (side) {
      const v = parseInt((side.textContent || '').trim(), 10);
      return isNaN(v) ? null : v;
    }
    return null;
  },

  _codeCell: function (row) {
    return row.querySelector('.d2h-code-line, .d2h-code-side-line');
  },

  _codeText: function (cell) {
    const ctn = cell.querySelector('.d2h-code-line-ctn');
    return ((ctn ? ctn.textContent : cell.textContent) || '').trim().slice(0, 240);
  },

  _attach: function (target) {
    const self = this;
    target.querySelectorAll('tr').forEach(function (row) {
      const cell = self._codeCell(row);
      if (!cell) return;
      const ln = self._newLineOf(row);
      if (ln === null) return; // no new-side line (pure deletion / spacer)
      cell.classList.add('dojo-clickable');
      row.addEventListener('click', function () {
        const file = self._fileOf(row);
        const text = self._codeText(cell);
        if (self._ref) self._ref.invokeMethodAsync('OnLineClicked', file, ln, text);
      });
    });
  },

  markFindings: function (elementId, findings) {
    const target = document.getElementById(elementId);
    if (!target) return;
    const self = this;
    target.querySelectorAll('tr.dojo-flagged').forEach(function (r) {
      r.classList.remove('dojo-flagged');
      const c = r.querySelector('.dojo-chip');
      if (c) c.remove();
    });
    (findings || []).forEach(function (f) {
      target.querySelectorAll('tr').forEach(function (row) {
        const cell = self._codeCell(row);
        if (!cell) return;
        if (self._fileOf(row) !== f.file) return;
        if (self._newLineOf(row) !== f.line) return;
        row.classList.add('dojo-flagged');
        if (!cell.querySelector('.dojo-chip')) {
          const chip = document.createElement('span');
          chip.className = 'dojo-chip cat-' + f.category;
          chip.textContent = f.category;
          cell.appendChild(chip);
        }
      });
    });
  }
};
