// Runs inside the DTEK shutdowns page in WebView2, once it has loaded.
// It reads what the page already has and makes the same request as the page's own address form.
// The host runs it again and again; the last expression returns the current result.
// The host puts the chosen address in place of the ADDRESS marker below.
(function () {
  if (window.__grafik) return;
  // No schedule data: this is not the schedule page but, for example, an Imperva check.
  if (typeof DisconSchedule === 'undefined' || !DisconSchedule.fact || typeof jQuery === 'undefined') return;
  if (document.readyState !== 'complete') return;

  var ADDRESS = /*ADDRESS*/;
  var page = DisconSchedule;

  window.__grafik = { state: 'pending' };

  function fail(message) {
    window.__grafik = { state: 'error', message: message };
  }

  // The warning of the "Шановні клієнти!" window, for example that the schedules do not apply now.
  function attentionText() {
    var node = document.querySelector('#modal-attention .m-attention__text');
    if (!node) return '';
    var copy = node.cloneNode(true);
    copy.querySelectorAll('br').forEach(function (br) { br.replaceWith('\n'); });
    copy.querySelectorAll('p,div').forEach(function (block) { block.append('\n'); });
    return copy.textContent
      .split('\n')
      .map(function (line) { return line.replace(/\s+/g, ' ').trim(); })
      .filter(Boolean)
      .join('\n');
  }

  function request(attemptsLeft) {
    jQuery.ajax({
      url: document.querySelector('meta[name="ajaxUrl"]').content,
      method: 'POST',
      dataType: 'json',
      timeout: 30000,
      data: {
        method: 'getHomeNum',
        data: [
          { name: 'city', value: ADDRESS.city },
          { name: 'street', value: ADDRESS.street },
          { name: 'updateFact', value: page.fact.update }
        ]
      }
    }).done(onAnswer).fail(function (xhr, status) {
      // On the first visit with a fresh profile the site answers 400 once; a repeat goes through.
      if (attemptsLeft > 0) {
        setTimeout(function () { request(attemptsLeft - 1); }, 1500);
        return;
      }
      fail('Сайт ДТЕК не відповів на запит за адресою (' + (xhr.status || status) + ').');
    });
  }

  function onAnswer(answer) {
    // When the whole street is in one queue, the site returns one entry under the key "-".
    var info = answer && answer.data && (answer.data[ADDRESS.house] || answer.data['-']);
    if (!info) {
      fail('Сайт ДТЕК не знайшов адресу: ' + ADDRESS.city + ', ' + ADDRESS.street + ', ' + ADDRESS.house + '.');
      return;
    }

    // The site adds fact and preset to the answer only when they are newer than those in the page.
    var fact = answer.fact || page.fact;
    var preset = answer.preset || page.preset;
    var groups = info.sub_type_reason || [];

    var factData = {};
    Object.keys(fact.data || {}).forEach(function (day) {
      factData[day] = {};
      groups.forEach(function (group) {
        if (fact.data[day][group]) factData[day][group] = fact.data[day][group];
      });
    });

    var plan = {};
    var names = {};
    groups.forEach(function (group) {
      if (preset.data && preset.data[group]) plan[group] = preset.data[group];
      names[group] = (preset.sch_names && preset.sch_names[group]) || group;
    });

    window.__grafik = {
      state: 'done',
      fetchedAt: Date.now(),
      address: ADDRESS,
      groups: groups,
      names: names,
      info: {
        type: info.type || '',
        subType: info.sub_type || '',
        start: info.start_date || '',
        end: info.end_date || '',
        cek: !!info.cek,
        voluntarily: !!info.voluntarily
      },
      flags: {
        showCurOutage: !!answer.showCurOutageParam,
        showCurSchedule: !!answer.showCurSchedule,
        showTableFact: !!answer.showTableFact,
        showTableSchedule: !!answer.showTableSchedule,
        showTablePlan: !!answer.showTablePlan
      },
      addressCheckedAt: answer.updateTimestamp || '',
      factUpdatedAt: fact.update || '',
      fact: factData,
      plan: plan,
      attention: attentionText()
    };
  }

  request(2);
})();
JSON.stringify(window.__grafik || null);
