// Runs inside the DTEK shutdowns page: the house numbers of one street, the way the page's form gets them.
// The host puts the settlement and the street in place of the CITY and STREET markers, and runs the script
// again until the answer is there.
(function () {
  if (typeof DisconSchedule === 'undefined' || !DisconSchedule.fact || typeof jQuery === 'undefined') return null;

  var city = /*CITY*/;
  var street = /*STREET*/;
  var key = city + '\n' + street;
  var cache = window.__bluettiHouses = window.__bluettiHouses || {};
  if (cache[key]) return JSON.stringify(cache[key]);

  cache[key] = { state: 'pending' };
  function request(attemptsLeft) {
    jQuery.ajax({
      url: document.querySelector('meta[name="ajaxUrl"]').content,
      method: 'POST',
      dataType: 'json',
      timeout: 30000,
      data: {
        method: 'getHomeNum',
        data: [
          { name: 'city', value: city },
          { name: 'street', value: street },
          { name: 'updateFact', value: DisconSchedule.fact.update }
        ]
      }
    }).done(function (answer) {
      cache[key] = { state: 'done', houses: Object.keys((answer && answer.data) || {}) };
    }).fail(function (xhr, status) {
      if (attemptsLeft > 0) {
        setTimeout(function () { request(attemptsLeft - 1); }, 1500);
        return;
      }
      cache[key] = { state: 'error', message: 'Сайт ДТЕК не відповів (' + (xhr.status || status) + ').' };
    });
  }

  request(2);
  return JSON.stringify(cache[key]);
})();
