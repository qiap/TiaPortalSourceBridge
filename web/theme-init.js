(() => {
  'use strict';let theme='dark';try{if(localStorage.getItem('tia-ui-theme')==='light')theme='light';}catch{}
  document.documentElement.dataset.theme=theme;
  document.documentElement.style.colorScheme=theme;
})();
