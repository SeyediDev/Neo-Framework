(() => {
  const menu = document.querySelector('.platform-menu');
  if (!menu) return;
  const mobile = window.matchMedia('(max-width:850px)');
  const update = () => { menu.open = !mobile.matches; };
  update();
  mobile.addEventListener('change', update);
})();
