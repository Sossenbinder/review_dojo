window.dojoDiff = {
  render: function (elementId, diffText, mode) {
    const target = document.getElementById(elementId);
    if (!target || typeof Diff2HtmlUI === 'undefined') return;
    const ui = new Diff2HtmlUI(target, diffText, {
      drawFileList: false,
      matching: 'lines',
      outputFormat: mode === 'side' ? 'side-by-side' : 'line-by-line'
    });
    ui.draw();
  }
};
