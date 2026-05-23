const e = React.createElement;

function apiUrl(action) {
  const base = document.body.dataset.api;
  return `${base}?action=${encodeURIComponent(action)}`;
}

function LaunchPadApp() {
  const user = JSON.parse(document.body.dataset.user || '{}');
  const [items, setItems] = React.useState([]);
  const [filter, setFilter] = React.useState('');
  const [modal, setModal] = React.useState(null);
  const [message, setMessage] = React.useState('');

  React.useEffect(() => {
    fetch(apiUrl('list_tcodes'))
      .then((res) => res.json())
      .then((json) => setItems(json.items || []))
      .catch(() => setMessage('Unable to load transaction directory.'));
  }, []);

  React.useEffect(() => {
    const input = document.getElementById('react-tcode-input');
    const button = document.getElementById('react-launch-button');
    const launch = () => launchCode(input.value);
    button?.addEventListener('click', launch);
    input?.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') launch();
    });
    return () => button?.removeEventListener('click', launch);
  }, [items]);

  const filtered = items.filter((item) => {
    const haystack = `${item.tcode} ${item.module_name} ${item.description}`.toLowerCase();
    return haystack.includes(filter.toLowerCase());
  });

  async function launchCode(code) {
    const tcode = String(code || '').trim().toUpperCase();
    if (!tcode) {
      setMessage('Enter a transaction code.');
      return;
    }
    const form = new FormData();
    form.set('tcode', tcode);
    const res = await fetch(apiUrl('launch_tcode'), { method: 'POST', body: form });
    const json = await res.json().catch(() => ({ ok: false, message: 'Unable to launch module.' }));
    if (!json.ok) {
      setMessage(json.message || 'Transaction code not found.');
      return;
    }
    setMessage('');
    setModal(json.item);
  }

  const kpis = [
    ['Active Modules', String(items.length), 'From transaction directory', 'TM'],
    ['Role', user.role || 'User', 'Current access profile', 'RA'],
    ['Status', user.status || 'Active', 'Account state', 'OK'],
    ['Today', new Date().toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' }), 'Local workspace date', 'DT'],
  ];

  return e('section', { className: 'workspace' },
    e('div', { className: 'hero' },
      e('div', null,
        e('h2', null, 'Launch Pad'),
        e('p', null, 'Search and launch validation modules using transaction codes.')
      ),
      e('div', { className: 'badge-row' },
        e('span', { className: 'badge badge-blue' }, 'Role: ', user.role || 'User'),
        e('span', { className: 'badge badge-green' }, 'Validated workspace')
      )
    ),
    message ? e('div', { className: 'mt-4 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm font-bold text-amber-700' }, message) : null,
    e('div', { className: 'kpi-grid' }, kpis.map((kpi) =>
      e('article', { className: 'kpi', key: kpi[0] },
        e('div', null, e('label', null, kpi[0]), e('strong', null, kpi[1]), e('span', null, kpi[2])),
        e('div', { className: 'kpi-icon' }, kpi[3])
      )
    )),
    e('div', { className: 'content-grid' },
      e('section', { className: 'surface' },
        e('div', { className: 'surface-head' },
          e('div', null, e('h3', null, 'Transaction Directory'), e('p', null, 'Search and launch controlled validation functions.')),
          e('input', { className: 'filter-input', value: filter, onChange: (event) => setFilter(event.target.value), placeholder: 'Filter directory...' })
        ),
        e('div', { className: 'surface-body' }, filtered.map((item) =>
          e('article', { className: 'tx-card', key: item.tcode, onClick: () => launchCode(item.tcode) },
            e('div', { className: 'tx-top' }, e('span', { className: 'code' }, item.tcode), e('span', { className: 'status' }, item.status)),
            e('h4', null, item.module_name),
            e('p', null, item.description)
          )
        ))
      ),
      e('section', { className: 'surface' },
        e('div', { className: 'surface-head' },
          e('div', null, e('h3', null, 'Modules'), e('p', null, 'Click a card to open a module placeholder.'))
        ),
        e('div', { className: 'surface-body module-grid' }, filtered.map((item) =>
          e('article', { className: 'module-card', key: `m-${item.tcode}`, onClick: () => launchCode(item.tcode) },
            e('div', { className: 'module-top' }, e('span', { className: 'code' }, item.tcode), e('span', { className: 'status' }, 'Active')),
            e('h4', null, item.module_name),
            e('p', null, item.function_details || item.description)
          )
        ))
      )
    ),
    modal ? e('div', { className: 'modal-backdrop' },
      e('div', { className: 'module-modal' },
        e('span', { className: 'code' }, modal.tcode),
        e('h2', { className: 'mt-4 text-2xl font-extrabold tracking-tight' }, modal.module_name),
        e('p', { className: 'mt-2' }, modal.description),
        e('p', { className: 'mt-3 rounded-2xl bg-slate-50 p-4' }, modal.function_details),
        e('div', { className: 'modal-actions' },
          e('button', { className: 'primary-btn', onClick: () => setModal(null) }, 'Close')
        )
      )
    ) : null
  );
}

ReactDOM.createRoot(document.getElementById('launchpad-root')).render(e(LaunchPadApp));
