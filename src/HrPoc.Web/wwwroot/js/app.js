// My Demo Corp — HR System (PoC) Frontend Application

const state = {
  activeTab: 'employees',
  empPage: 1,
  empPageSize: 25,
  empTotalPages: 1,
  departments: [],
  costCenters: [],
  locations: [],
  countries: [],
  deptViewMode: 'table',
  ccViewMode: 'table'
};

// Utility: API helper using relative path so IIS virtual directories work seamlessly
async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const res = await fetch(path, { ...options, headers });
  let data = null;
  const text = await res.text();
  if (text) {
    try { data = JSON.parse(text); } catch { data = { message: text }; }
  }
  if (!res.ok) {
    throw new Error((data && data.message) || `HTTP ${res.status}: ${res.statusText}`);
  }
  return data;
}

function showToast(message, type = 'success') {
  const container = document.getElementById('toastContainer');
  const el = document.createElement('div');
  el.className = `toast toast-${type}`;
  el.textContent = message;
  container.appendChild(el);
  setTimeout(() => el.remove(), 3800);
}

function esc(val) {
  if (val === null || val === undefined) return '';
  return String(val)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function formatDate(d) {
  if (!d) return '—';
  return String(d).slice(0, 10);
}

function formatDateTime(dt) {
  if (!dt) return '—';
  const d = new Date(dt);
  if (isNaN(d.getTime())) return String(dt);
  return d.toISOString().replace('T', ' ').slice(0, 19);
}

// Initialization
document.addEventListener('DOMContentLoaded', async () => {
  bindNavigation();
  bindModals();
  bindEmployeeLookupInputs();
  bindEmployeeEvents();
  bindDepartmentEvents();
  bindCostCenterEvents();
  bindLocationEvents();

  await refreshAllReferenceData();
  await loadEmployees();
});

async function refreshAllReferenceData() {
  try {
    const [stats, depts, ccs, locs] = await Promise.all([
      api('api/dashboard/stats'),
      api('api/departments'),
      api('api/costcenters'),
      api('api/locations')
    ]);

    state.departments = depts;
    state.costCenters = ccs;
    state.locations = locs;
    state.countries = stats.countries || [];

    renderDashboardStats(stats);
    populateFilterDropdowns();
    renderDepartments();
    renderCostCenters();
    renderLocations();
  } catch (err) {
    showToast(`Failed to load initial data: ${err.message}`, 'error');
  }
}

function renderDashboardStats(stats) {
  document.getElementById('statTotalEmployees').textContent = stats.totalEmployees.toLocaleString();
  document.getElementById('statActiveEmployees').textContent = stats.activeEmployees.toLocaleString();
  document.getElementById('statOnLeaveEmployees').textContent = stats.onLeaveEmployees.toLocaleString();
  document.getElementById('statTerminatedEmployees').textContent = stats.terminatedEmployees.toLocaleString();

  document.getElementById('statTotalDepartments').textContent = stats.totalDepartments.toLocaleString();
  document.getElementById('statTotalCostCenters').textContent = stats.totalCostCenters.toLocaleString();
  document.getElementById('statTotalLocations').textContent = stats.totalLocations.toLocaleString();
  document.getElementById('statTotalCountries').textContent = (stats.countries || []).length;

  document.getElementById('tabCountEmployees').textContent = stats.totalEmployees.toLocaleString();
  document.getElementById('tabCountDepartments').textContent = stats.totalDepartments.toLocaleString();
  document.getElementById('tabCountCostCenters').textContent = stats.totalCostCenters.toLocaleString();
  document.getElementById('tabCountLocations').textContent = stats.totalLocations.toLocaleString();

  const badge = document.getElementById('dbProviderBadge');
  badge.textContent = `DB: ${stats.databaseProvider}`;

  const recentBody = document.getElementById('recentTableBody');
  recentBody.innerHTML = (stats.recentEmployees || []).map(e => `
    <tr>
      <td class="mono">${esc(formatDateTime(e.updatedAt))}</td>
      <td class="mono">${esc(e.personnelNumber)}</td>
      <td class="mono"><strong>${esc(e.username)}</strong></td>
      <td>${esc(e.fullName)}</td>
      <td>${esc(e.jobTitle || '—')}</td>
      <td><span class="status-badge status-${esc(e.employmentStatus)}">${esc(e.employmentStatus)}</span></td>
      <td><span class="subtext">${esc(e.departmentStructuredName || '—')}</span></td>
      <td class="text-right">
        <button class="btn btn-outline btn-xs" onclick="openEditEmployee(${e.id})">Edit</button>
      </td>
    </tr>
  `).join('');
}

function populateFilterDropdowns() {
  // Department dropdowns
  const deptOptions = state.departments.map(d =>
    `<option value="${d.id}">${esc(d.structuredName)} (${d.employeeCount})</option>`
  ).join('');
  const empDeptFilter = document.getElementById('empDeptFilter');
  const prevDept = empDeptFilter.value;
  empDeptFilter.innerHTML = `<option value="">All Departments</option>` + deptOptions;
  empDeptFilter.value = prevDept;

  document.getElementById('empDepartmentId').innerHTML =
    `<option value="">— None —</option>` +
    state.departments.map(d => `<option value="${d.id}">${esc(d.structuredName)}</option>`).join('');

  document.getElementById('deptParentId').innerHTML =
    `<option value="">— Root (My Demo Corp) —</option>` +
    state.departments.map(d => `<option value="${d.id}">${esc(d.structuredName)}</option>`).join('');

  // Cost Center dropdowns
  const ccOptions = state.costCenters.map(c =>
    `<option value="${c.id}">${esc(c.structuredName)} — ${esc(c.shortName || '')} (${c.employeeCount})</option>`
  ).join('');
  const empCcFilter = document.getElementById('empCcFilter');
  const prevCc = empCcFilter.value;
  empCcFilter.innerHTML = `<option value="">All Cost Centers</option>` + ccOptions;
  empCcFilter.value = prevCc;

  document.getElementById('empCostCenterId').innerHTML =
    `<option value="">— None —</option>` +
    state.costCenters.map(c => `<option value="${c.id}">${esc(c.structuredName)} (${esc(c.shortName || c.code)})</option>`).join('');

  document.getElementById('ccParentId').innerHTML =
    `<option value="">— Top Level —</option>` +
    state.costCenters.map(c => `<option value="${c.id}">${esc(c.structuredName)} (${esc(c.shortName || c.code)})</option>`).join('');

  // Location dropdowns
  const locOptions = state.locations.map(l =>
    `<option value="${l.id}">${esc(l.structuredName)} (${l.employeeCount})</option>`
  ).join('');
  const empLocFilter = document.getElementById('empLocFilter');
  const prevLoc = empLocFilter.value;
  empLocFilter.innerHTML = `<option value="">All Locations</option>` + locOptions;
  empLocFilter.value = prevLoc;

  document.getElementById('empLocationId').innerHTML =
    `<option value="">— None —</option>` +
    state.locations.map(l => `<option value="${l.id}">${esc(l.structuredName)}</option>`).join('');

  // Countries
  const countryOptions = state.countries.map(c => `<option value="${esc(c)}">${esc(c)}</option>`).join('');
  const empCountryFilter = document.getElementById('empCountryFilter');
  const prevCountry = empCountryFilter.value;
  empCountryFilter.innerHTML = `<option value="">All</option>` + countryOptions;
  empCountryFilter.value = prevCountry;

  const locCountryFilter = document.getElementById('locCountryFilter');
  const prevLocCountry = locCountryFilter.value;
  locCountryFilter.innerHTML = `<option value="">All Countries</option>` + countryOptions;
  locCountryFilter.value = prevLocCountry;
}

// Navigation & Topbar
function bindNavigation() {
  document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => switchTab(btn.dataset.tab));
  });

  document.querySelectorAll('.kpi-clickable').forEach(card => {
    card.addEventListener('click', () => {
      const tab = card.dataset.tab;
      switchTab(tab);
    });
  });

  document.getElementById('btnPrimaryAdd').addEventListener('click', () => {
    if (state.activeTab === 'departments') openCreateDepartment();
    else if (state.activeTab === 'costcenters') openCreateCostCenter();
    else if (state.activeTab === 'locations') openCreateLocation();
    else openCreateEmployee();
  });

  document.getElementById('btnReseed').addEventListener('click', async () => {
    if (!confirm('Reset the database and reload all 4 CSV files (MDC-HR-Data, MDC-Department, MDC-Costcenter, MDC-Locations)? Any manual edits will be overwritten.')) {
      return;
    }
    try {
      const res = await api('api/dashboard/reseed', { method: 'POST' });
      showToast(res.message);
      await refreshAllReferenceData();
      state.empPage = 1;
      await loadEmployees();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnRefreshRecent').addEventListener('click', refreshAllReferenceData);
}

function switchTab(tabName) {
  state.activeTab = tabName;
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === tabName));
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.toggle('active', p.id === `tab-${tabName}`));

  const primaryBtn = document.getElementById('btnPrimaryAdd');
  if (tabName === 'departments') primaryBtn.textContent = '+ New Department';
  else if (tabName === 'costcenters') primaryBtn.textContent = '+ New Cost Center';
  else if (tabName === 'locations') primaryBtn.textContent = '+ New Location';
  else primaryBtn.textContent = '+ New Employee';
}

function bindModals() {
  document.querySelectorAll('[data-close]').forEach(btn => {
    btn.addEventListener('click', () => closeModal(btn.dataset.close));
  });

  // Close open modals on Escape key press (if employee lookup popover is open, close it first)
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      const popover = document.getElementById('empLookupPopover');
      if (popover && !popover.classList.contains('hidden')) {
        closeEmployeeLookupPopover();
        return;
      }
      document.querySelectorAll('.modal-backdrop:not(.hidden)').forEach(modal => {
        modal.classList.add('hidden');
      });
    }
  });
}

function openModal(id) {
  closeEmployeeLookupPopover();
  document.getElementById(id).classList.remove('hidden');
  syncLookupClearButtons();
}

function closeModal(id) {
  closeEmployeeLookupPopover();
  document.getElementById(id).classList.add('hidden');
}

// ============================================================================
// SERVER-SIDE EMPLOYEE AUTOCOMPLETE (Supports 5,000+ / 50,000+ Employees)
// ============================================================================
const LOOKUP_INPUT_IDS = ['deptManager', 'deptDeputy', 'ccManager', 'locManager', 'locAssistant'];
let activeLookupInput = null;
let lookupItems = [];
let lookupActiveIndex = -1;
let lookupDebounceTimer = null;
let lookupReqCounter = 0;

function closeEmployeeLookupPopover() {
  const popover = document.getElementById('empLookupPopover');
  if (popover) {
    popover.classList.add('hidden');
    popover.innerHTML = '';
  }
  activeLookupInput = null;
  lookupItems = [];
  lookupActiveIndex = -1;
}

function syncLookupClearButtons() {
  LOOKUP_INPUT_IDS.forEach(id => {
    const input = document.getElementById(id);
    if (!input || !input.parentElement) return;
    const clearBtn = input.parentElement.querySelector('.emp-lookup-clear');
    if (clearBtn) {
      clearBtn.classList.toggle('hidden', !input.value.trim());
    }
  });
}

function positionEmployeeLookupPopover(input) {
  const popover = document.getElementById('empLookupPopover');
  if (!popover || !input) return;

  const rect = input.getBoundingClientRect();
  const viewportHeight = window.innerHeight;
  const spaceBelow = viewportHeight - rect.bottom - 12;
  const spaceAbove = rect.top - 12;
  const desiredMaxHeight = 270;

  popover.style.width = `${Math.max(rect.width, 300)}px`;
  popover.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - Math.max(rect.width, 300) - 8))}px`;

  if (spaceBelow >= 180 || spaceBelow >= spaceAbove) {
    popover.style.top = `${rect.bottom + 4}px`;
    popover.style.bottom = 'auto';
    popover.style.maxHeight = `${Math.min(desiredMaxHeight, Math.max(140, spaceBelow))}px`;
  } else {
    popover.style.bottom = `${viewportHeight - rect.top + 4}px`;
    popover.style.top = 'auto';
    popover.style.maxHeight = `${Math.min(desiredMaxHeight, Math.max(140, spaceAbove))}px`;
  }
}

async function fetchAndRenderEmployeeLookup(input, queryText, isInitialFocus = false) {
  const popover = document.getElementById('empLookupPopover');
  if (!popover || !input) return;

  activeLookupInput = input;
  const reqId = ++lookupReqCounter;
  const term = (queryText || '').trim();

  try {
    let results = await api(`api/employees/lookup?q=${encodeURIComponent(term)}&limit=50`);
    if (reqId !== lookupReqCounter || activeLookupInput !== input) return;

    // If opened on focus when field already holds a single exact username, also load top 50 so user can browse
    if (isInitialFocus && term && results.length <= 1) {
      const topList = await api('api/employees/lookup?limit=50');
      if (reqId !== lookupReqCounter || activeLookupInput !== input) return;
      const seen = new Set(results.map(r => r.username));
      topList.forEach(item => {
        if (!seen.has(item.username)) {
          results.push(item);
        }
      });
    }

    lookupItems = results || [];
    lookupActiveIndex = lookupItems.length > 0 ? 0 : -1;

    const headerLabel = term
      ? `Matches for "${esc(term)}" (showing up to 50)`
      : `Type Name, Username, or Personnel # to search all employees`;

    if (lookupItems.length === 0) {
      popover.innerHTML = `
        <div class="emp-lookup-header"><span>${headerLabel}</span></div>
        <div class="emp-lookup-empty">No employees matching "${esc(term)}" — you can still enter a custom username.</div>
      `;
    } else {
      popover.innerHTML = `
        <div class="emp-lookup-header"><span>${headerLabel}</span></div>
        <div class="emp-lookup-list">
          ${lookupItems.map((e, idx) => `
            <div class="emp-lookup-item ${idx === lookupActiveIndex ? 'active' : ''}" data-idx="${idx}" data-username="${esc(e.username)}">
              <div class="emp-lookup-main">
                <span class="mono emp-lookup-user">${esc(e.username)}</span>
                <span class="emp-lookup-name">${esc(e.fullName)}</span>
                <span class="mono emp-lookup-pnum">#${esc(e.personnelNumber)}</span>
              </div>
              <div class="emp-lookup-sub">
                ${esc(e.jobTitle || 'Employee')} &bull; ${esc(e.employmentStatus || 'Active')}
              </div>
            </div>
          `).join('')}
        </div>
      `;
    }

    popover.classList.remove('hidden');
    positionEmployeeLookupPopover(input);

    popover.querySelectorAll('.emp-lookup-item').forEach(el => {
      el.addEventListener('mousedown', (ev) => {
        ev.preventDefault();
        selectLookupEmployee(el.dataset.username);
      });
    });
  } catch {
    // Ignore transient network errors during typing
  }
}

function selectLookupEmployee(username) {
  if (!activeLookupInput) return;
  activeLookupInput.value = username || '';
  syncLookupClearButtons();
  closeEmployeeLookupPopover();
}

function updateLookupHighlight() {
  const popover = document.getElementById('empLookupPopover');
  if (!popover) return;
  const nodes = popover.querySelectorAll('.emp-lookup-item');
  nodes.forEach((node, idx) => {
    const isActive = idx === lookupActiveIndex;
    node.classList.toggle('active', isActive);
    if (isActive) {
      node.scrollIntoView({ block: 'nearest' });
    }
  });
}

function bindEmployeeLookupInputs() {
  LOOKUP_INPUT_IDS.forEach(id => {
    const input = document.getElementById(id);
    if (!input) return;

    // Wrap input to place the clear (x) button inside
    const wrap = document.createElement('div');
    wrap.className = 'emp-lookup-wrap';
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);

    const clearBtn = document.createElement('button');
    clearBtn.type = 'button';
    clearBtn.className = 'emp-lookup-clear hidden';
    clearBtn.title = 'Clear username and search all employees';
    clearBtn.innerHTML = '&times;';
    wrap.appendChild(clearBtn);

    clearBtn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      input.value = '';
      syncLookupClearButtons();
      input.focus();
      fetchAndRenderEmployeeLookup(input, '', true);
    });

    input.addEventListener('focus', () => {
      syncLookupClearButtons();
      fetchAndRenderEmployeeLookup(input, input.value, true);
    });

    input.addEventListener('click', () => {
      const popover = document.getElementById('empLookupPopover');
      if (popover && popover.classList.contains('hidden')) {
        fetchAndRenderEmployeeLookup(input, input.value, true);
      }
    });

    input.addEventListener('input', () => {
      syncLookupClearButtons();
      clearTimeout(lookupDebounceTimer);
      lookupDebounceTimer = setTimeout(() => {
        fetchAndRenderEmployeeLookup(input, input.value, false);
      }, 150);
    });

    input.addEventListener('keydown', (e) => {
      const popover = document.getElementById('empLookupPopover');
      const isOpen = popover && !popover.classList.contains('hidden') && activeLookupInput === input;

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        if (!isOpen) {
          fetchAndRenderEmployeeLookup(input, input.value, false);
        } else if (lookupItems.length > 0) {
          lookupActiveIndex = (lookupActiveIndex + 1) % lookupItems.length;
          updateLookupHighlight();
        }
      } else if (e.key === 'ArrowUp') {
        if (isOpen && lookupItems.length > 0) {
          e.preventDefault();
          lookupActiveIndex = (lookupActiveIndex - 1 + lookupItems.length) % lookupItems.length;
          updateLookupHighlight();
        }
      } else if (e.key === 'Enter') {
        if (isOpen && lookupActiveIndex >= 0 && lookupActiveIndex < lookupItems.length) {
          e.preventDefault();
          selectLookupEmployee(lookupItems[lookupActiveIndex].username);
        }
      } else if (e.key === 'Tab') {
        closeEmployeeLookupPopover();
      }
    });

    input.addEventListener('blur', () => {
      setTimeout(() => {
        if (document.activeElement !== input) {
          closeEmployeeLookupPopover();
        }
      }, 150);
    });
  });

  window.addEventListener('resize', () => {
    if (activeLookupInput) positionEmployeeLookupPopover(activeLookupInput);
  });

  document.querySelectorAll('.modal-body').forEach(mb => {
    mb.addEventListener('scroll', () => {
      if (activeLookupInput) positionEmployeeLookupPopover(activeLookupInput);
    });
  });
}

// ============================================================================
// 1. EMPLOYEES
// ============================================================================
function bindEmployeeEvents() {
  let debounceTimer = null;
  document.getElementById('empSearch').addEventListener('input', () => {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      state.empPage = 1;
      loadEmployees();
    }, 260);
  });

  ['empStatusFilter', 'empDeptFilter', 'empCcFilter', 'empLocFilter', 'empCountryFilter', 'empSortBy', 'empSortDir'].forEach(id => {
    document.getElementById(id).addEventListener('change', () => {
      state.empPage = 1;
      loadEmployees();
    });
  });

  document.getElementById('empPageSize').addEventListener('change', (e) => {
    state.empPageSize = parseInt(e.target.value, 10) || 25;
    state.empPage = 1;
    loadEmployees();
  });

  document.getElementById('btnClearEmpFilters').addEventListener('click', () => {
    document.getElementById('empSearch').value = '';
    document.getElementById('empStatusFilter').value = '';
    document.getElementById('empDeptFilter').value = '';
    document.getElementById('empCcFilter').value = '';
    document.getElementById('empLocFilter').value = '';
    document.getElementById('empCountryFilter').value = '';
    state.empPage = 1;
    loadEmployees();
  });

  document.getElementById('btnEmpFirst').addEventListener('click', () => { state.empPage = 1; loadEmployees(); });
  document.getElementById('btnEmpPrev').addEventListener('click', () => { if (state.empPage > 1) { state.empPage--; loadEmployees(); } });
  document.getElementById('btnEmpNext').addEventListener('click', () => { if (state.empPage < state.empTotalPages) { state.empPage++; loadEmployees(); } });
  document.getElementById('btnEmpLast').addEventListener('click', () => { state.empPage = state.empTotalPages; loadEmployees(); });

  document.getElementById('btnGenerateIds').addEventListener('click', async () => {
    const fn = document.getElementById('empFirstName').value;
    const ln = document.getElementById('empLastName').value;
    try {
      const suggestion = await api(`api/employees/next-identifiers?firstName=${encodeURIComponent(fn)}&lastName=${encodeURIComponent(ln)}`);
      if (!document.getElementById('empPersonnelNumber').value) {
        document.getElementById('empPersonnelNumber').value = suggestion.personnelNumber;
      }
      if (suggestion.username) document.getElementById('empUsername').value = suggestion.username;
      if (suggestion.initials) document.getElementById('empInitials').value = suggestion.initials;
      if (suggestion.email) document.getElementById('empEmail').value = suggestion.email;
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnCopyLocAddress').addEventListener('click', () => {
    const locId = parseInt(document.getElementById('empLocationId').value, 10);
    const loc = state.locations.find(l => l.id === locId);
    if (!loc) {
      showToast('Select a Location first to copy its address.', 'error');
      return;
    }
    document.getElementById('empCity').value = loc.city || '';
    document.getElementById('empStreet').value = loc.street || '';
    document.getElementById('empZipCode').value = loc.zipCode || '';
    if (loc.fax) document.getElementById('empFax').value = loc.fax;
    showToast(`Copied address from ${loc.city || loc.name}`);
  });

  document.getElementById('empStatus').addEventListener('change', (e) => {
    const termInput = document.getElementById('empTerminationDate');
    if (e.target.value === 'Terminated' && !termInput.value) {
      termInput.value = new Date().toISOString().slice(0, 10);
    } else if (e.target.value === 'Active') {
      termInput.value = '';
    }
  });

  document.getElementById('employeeForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = document.getElementById('empId').value;
    const payload = {
      username: document.getElementById('empUsername').value,
      personnelNumber: document.getElementById('empPersonnelNumber').value,
      firstName: document.getElementById('empFirstName').value,
      lastName: document.getElementById('empLastName').value,
      initials: document.getElementById('empInitials').value || null,
      email: document.getElementById('empEmail').value || null,
      jobTitle: document.getElementById('empJobTitle').value || null,
      employmentStatus: document.getElementById('empStatus').value,
      hireDate: document.getElementById('empHireDate').value || null,
      terminationDate: document.getElementById('empTerminationDate').value || null,
      phone: document.getElementById('empPhone').value || null,
      fax: document.getElementById('empFax').value || null,
      street: document.getElementById('empStreet').value || null,
      city: document.getElementById('empCity').value || null,
      zipCode: document.getElementById('empZipCode').value || null,
      departmentId: parseInt(document.getElementById('empDepartmentId').value, 10) || null,
      costCenterId: parseInt(document.getElementById('empCostCenterId').value, 10) || null,
      locationId: parseInt(document.getElementById('empLocationId').value, 10) || null
    };

    try {
      if (id) {
        await api(`api/employees/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
        showToast(`Employee ${payload.username} updated.`);
      } else {
        await api('api/employees', { method: 'POST', body: JSON.stringify(payload) });
        showToast(`Employee ${payload.username} created.`);
      }
      closeModal('employeeModal');
      await Promise.all([loadEmployees(), refreshAllReferenceData()]);
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnDeleteEmployee').addEventListener('click', async () => {
    const id = document.getElementById('empId').value;
    const username = document.getElementById('empUsername').value;
    if (!id || !confirm(`Permanently delete employee ${username}?`)) return;
    try {
      await api(`api/employees/${id}`, { method: 'DELETE' });
      showToast(`Employee ${username} deleted.`);
      closeModal('employeeModal');
      await Promise.all([loadEmployees(), refreshAllReferenceData()]);
    } catch (err) {
      showToast(err.message, 'error');
    }
  });
}

async function loadEmployees() {
  const params = new URLSearchParams({
    page: state.empPage,
    pageSize: state.empPageSize,
    sortBy: document.getElementById('empSortBy').value,
    sortDir: document.getElementById('empSortDir').value
  });

  const search = document.getElementById('empSearch').value.trim();
  const status = document.getElementById('empStatusFilter').value;
  const deptId = document.getElementById('empDeptFilter').value;
  const ccId = document.getElementById('empCcFilter').value;
  const locId = document.getElementById('empLocFilter').value;
  const country = document.getElementById('empCountryFilter').value;

  if (search) params.set('search', search);
  if (status) params.set('status', status);
  if (deptId) params.set('departmentId', deptId);
  if (ccId) params.set('costCenterId', ccId);
  if (locId) params.set('locationId', locId);
  if (country) params.set('country', country);

  try {
    const data = await api(`api/employees?${params.toString()}`);
    state.empTotalPages = Math.max(1, data.totalPages || 1);

    const start = data.totalCount === 0 ? 0 : (data.page - 1) * data.pageSize + 1;
    const end = Math.min(data.totalCount, data.page * data.pageSize);
    document.getElementById('empResultSummary').textContent =
      `Showing ${start.toLocaleString()}–${end.toLocaleString()} of ${data.totalCount.toLocaleString()} employees`;

    document.getElementById('empPageInfo').textContent =
      `Page ${data.page} of ${state.empTotalPages}`;

    document.getElementById('btnEmpFirst').disabled = data.page <= 1;
    document.getElementById('btnEmpPrev').disabled = data.page <= 1;
    document.getElementById('btnEmpNext').disabled = data.page >= state.empTotalPages;
    document.getElementById('btnEmpLast').disabled = data.page >= state.empTotalPages;

    const tbody = document.getElementById('empTableBody');
    if (!data.items || data.items.length === 0) {
      tbody.innerHTML = `<tr><td colspan="10" style="text-align:center;padding:24px;color:#64748b;">No employees match the current filters.</td></tr>`;
      return;
    }

    tbody.innerHTML = data.items.map(e => `
      <tr>
        <td class="mono">${esc(e.personnelNumber)}</td>
        <td class="mono"><strong>${esc(e.username)}</strong></td>
        <td>
          <strong>${esc(e.lastName)}, ${esc(e.firstName)}</strong>
          <span class="subtext">${esc(e.email || '')} ${e.phone ? '&bull; ' + esc(e.phone) : ''}</span>
        </td>
        <td>${esc(e.jobTitle || '—')}</td>
        <td>
          <span class="status-badge status-${esc(e.employmentStatus)}">${esc(e.employmentStatus)}</span>
          ${e.terminationDate ? `<span class="subtext">End: ${esc(formatDate(e.terminationDate))}</span>` : ''}
        </td>
        <td class="mono">${esc(formatDate(e.hireDate))}</td>
        <td>
          ${esc(e.departmentName || '—')}
          <span class="subtext" title="${esc(e.departmentStructuredName)}">${esc(e.departmentShortName || e.departmentStructuredName || '')}</span>
        </td>
        <td>
          <span class="mono">${esc(e.costCenterStructuredName || '—')}</span>
          <span class="subtext">${esc(e.costCenterShortName || '')}</span>
        </td>
        <td>
          ${e.countryShortcut ? `<span class="country-pill">${esc(e.countryShortcut)}</span>` : ''}
          ${esc(e.city || e.locationName || '—')}
          <span class="subtext">${esc(e.street || '')}</span>
        </td>
        <td class="text-right">
          <button class="btn btn-outline btn-xs" onclick="openEditEmployee(${e.id})">Edit</button>
        </td>
      </tr>
    `).join('');
  } catch (err) {
    showToast(`Failed to load employees: ${err.message}`, 'error');
  }
}

async function openCreateEmployee() {
  document.getElementById('empModalTitle').textContent = 'New Employee (Onboarding)';
  document.getElementById('employeeForm').reset();
  document.getElementById('empId').value = '';
  document.getElementById('empStatus').value = 'Active';
  document.getElementById('empHireDate').value = new Date().toISOString().slice(0, 10);
  document.getElementById('empLeadershipBanner').classList.add('hidden');
  document.getElementById('empMetaFooter').textContent = '';
  document.getElementById('btnDeleteEmployee').classList.add('hidden');

  try {
    const suggestion = await api('api/employees/next-identifiers');
    document.getElementById('empPersonnelNumber').value = suggestion.personnelNumber;
  } catch {}

  openModal('employeeModal');
}

async function openEditEmployee(id) {
  try {
    const data = await api(`api/employees/${id}`);
    const e = data.employee;
    document.getElementById('empModalTitle').textContent = `Edit Employee — ${e.firstName} ${e.lastName} (${e.username})`;
    document.getElementById('empId').value = e.id;
    document.getElementById('empFirstName').value = e.firstName || '';
    document.getElementById('empLastName').value = e.lastName || '';
    document.getElementById('empInitials').value = e.initials || '';
    document.getElementById('empStatus').value = e.employmentStatus || 'Active';
    document.getElementById('empPersonnelNumber').value = e.personnelNumber || '';
    document.getElementById('empUsername').value = e.username || '';
    document.getElementById('empHireDate').value = formatDate(e.hireDate) !== '—' ? formatDate(e.hireDate) : '';
    document.getElementById('empTerminationDate').value = e.terminationDate ? formatDate(e.terminationDate) : '';
    document.getElementById('empJobTitle').value = e.jobTitle || '';
    document.getElementById('empEmail').value = e.email || '';
    document.getElementById('empDepartmentId').value = e.departmentId || '';
    document.getElementById('empCostCenterId').value = e.costCenterId || '';
    document.getElementById('empLocationId').value = e.locationId || '';
    document.getElementById('empPhone').value = e.phone || '';
    document.getElementById('empFax').value = e.fax || '';
    document.getElementById('empZipCode').value = e.zipCode || '';
    document.getElementById('empCity').value = e.city || '';
    document.getElementById('empStreet').value = e.street || '';

    const roles = [];
    (data.managedDepartments || []).forEach(d => roles.push(`${d.role} of Department "${d.structuredName}"`));
    (data.managedCostCenters || []).forEach(c => roles.push(`Manager of Cost Center "${c.structuredName}"`));
    (data.managedLocations || []).forEach(l => roles.push(`${l.role} of Location "${l.structuredName}"`));

    const banner = document.getElementById('empLeadershipBanner');
    if (roles.length > 0) {
      banner.innerHTML = `<strong>Leadership Roles:</strong> ${roles.map(esc).join(' &bull; ')}`;
      banner.classList.remove('hidden');
    } else {
      banner.classList.add('hidden');
    }

    document.getElementById('empMetaFooter').innerHTML =
      `<span>Record ID: #${e.id}</span><span>Last Updated (UTC): ${esc(formatDateTime(e.updatedAt))}</span>`;

    document.getElementById('btnDeleteEmployee').classList.remove('hidden');
    openModal('employeeModal');
  } catch (err) {
    showToast(err.message, 'error');
  }
}

function filterEmployeesBy(field, value) {
  document.getElementById('empSearch').value = '';
  document.getElementById('empStatusFilter').value = '';
  document.getElementById('empDeptFilter').value = field === 'departmentId' ? String(value) : '';
  document.getElementById('empCcFilter').value = field === 'costCenterId' ? String(value) : '';
  document.getElementById('empLocFilter').value = field === 'locationId' ? String(value) : '';
  document.getElementById('empCountryFilter').value = '';
  switchTab('employees');
  state.empPage = 1;
  loadEmployees();
}

// ============================================================================
// 2. DEPARTMENTS
// ============================================================================
function bindDepartmentEvents() {
  document.getElementById('deptSearch').addEventListener('input', renderDepartments);
  document.getElementById('btnAddDepartment').addEventListener('click', openCreateDepartment);

  document.getElementById('btnDeptTableView').addEventListener('click', () => {
    state.deptViewMode = 'table';
    document.getElementById('btnDeptTableView').classList.add('active');
    document.getElementById('btnDeptTreeView').classList.remove('active');
    document.getElementById('deptTableContainer').classList.remove('hidden');
    document.getElementById('deptTreeContainer').classList.add('hidden');
  });

  document.getElementById('btnDeptTreeView').addEventListener('click', () => {
    state.deptViewMode = 'tree';
    document.getElementById('btnDeptTreeView').classList.add('active');
    document.getElementById('btnDeptTableView').classList.remove('active');
    document.getElementById('deptTreeContainer').classList.remove('hidden');
    document.getElementById('deptTableContainer').classList.add('hidden');
  });

  document.getElementById('departmentForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = document.getElementById('deptId').value;
    const payload = {
      name: document.getElementById('deptName').value,
      shortName: document.getElementById('deptShortName').value || null,
      parentDepartmentId: parseInt(document.getElementById('deptParentId').value, 10) || null,
      structuredName: document.getElementById('deptStructuredName').value || null,
      functionalShortcut: document.getElementById('deptFunctionalShortcut').value || null,
      managerUsername: document.getElementById('deptManager').value || null,
      deputyManagerUsername: document.getElementById('deptDeputy').value || null,
      adContainerForNewHires: document.getElementById('deptAdContainer').value || null
    };
    try {
      if (id) {
        await api(`api/departments/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
        showToast('Department updated.');
      } else {
        await api('api/departments', { method: 'POST', body: JSON.stringify(payload) });
        showToast('Department created.');
      }
      closeModal('departmentModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnDeleteDepartment').addEventListener('click', async () => {
    const id = document.getElementById('deptId').value;
    const name = document.getElementById('deptStructuredName').value;
    if (!id || !confirm(`Delete department "${name}"?`)) return;
    try {
      await api(`api/departments/${id}`, { method: 'DELETE' });
      showToast('Department deleted.');
      closeModal('departmentModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });
}

function renderDepartments() {
  const term = (document.getElementById('deptSearch').value || '').trim().toLowerCase();
  const filtered = state.departments.filter(d =>
    !term ||
    (d.name && d.name.toLowerCase().includes(term)) ||
    (d.shortName && d.shortName.toLowerCase().includes(term)) ||
    (d.structuredName && d.structuredName.toLowerCase().includes(term)) ||
    (d.managerUsername && d.managerUsername.toLowerCase().includes(term)) ||
    (d.deputyManagerUsername && d.deputyManagerUsername.toLowerCase().includes(term))
  );

  const tbody = document.getElementById('deptTableBody');
  tbody.innerHTML = filtered.map(d => `
    <tr>
      <td><strong>${esc(d.name)}</strong></td>
      <td class="mono">${esc(d.shortName || '—')}</td>
      <td>
        <span>${esc(d.structuredName)}</span>
        <span class="subtext mono">${esc(d.adContainerForNewHires || '')}</span>
      </td>
      <td><span class="country-pill">L${d.treeLevel}</span></td>
      <td class="mono">${esc(d.managerUsername || '—')}</td>
      <td class="mono">${esc(d.deputyManagerUsername || '—')}</td>
      <td>
        <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('departmentId', ${d.id})">
          ${d.employeeCount} staff
        </button>
      </td>
      <td class="text-right">
        <button class="btn btn-outline btn-xs" onclick="openEditDepartment(${d.id})">Edit</button>
      </td>
    </tr>
  `).join('');

  // Render hierarchy tree
  const treeContainer = document.getElementById('deptTreeContainer');
  const byParent = new Map();
  filtered.forEach(d => {
    const pid = d.parentDepartmentId || 0;
    if (!byParent.has(pid)) byParent.set(pid, []);
    byParent.get(pid).push(d);
  });

  function buildNodes(parentId) {
    const children = byParent.get(parentId) || [];
    if (children.length === 0) return '';
    return children.map(d => `
      <div class="tree-node">
        <div class="tree-item">
          <div>
            <strong>${esc(d.name)}</strong>
            <span class="mono" style="color:#64748b;margin-left:6px;">(${esc(d.shortName || d.structuredName)})</span>
            ${d.managerUsername ? `<span class="country-pill" style="margin-left:8px;">Mgr: ${esc(d.managerUsername)}</span>` : ''}
          </div>
          <div style="display:flex;gap:6px;">
            <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('departmentId', ${d.id})">${d.employeeCount} staff</button>
            <button class="btn btn-outline btn-xs" onclick="openEditDepartment(${d.id})">Edit</button>
          </div>
        </div>
        ${buildNodes(d.id)}
      </div>
    `).join('');
  }

  const rootIds = new Set(filtered.map(d => d.id));
  const topNodes = filtered.filter(d => !d.parentDepartmentId || !rootIds.has(d.parentDepartmentId));
  treeContainer.innerHTML = `<div class="tree-root">` + topNodes.map(d => `
    <div class="tree-node">
      <div class="tree-item">
        <div>
          <strong>${esc(d.name)}</strong>
          <span class="mono" style="color:#64748b;margin-left:6px;">(${esc(d.shortName || d.structuredName)})</span>
          ${d.managerUsername ? `<span class="country-pill" style="margin-left:8px;">Mgr: ${esc(d.managerUsername)}</span>` : ''}
        </div>
        <div style="display:flex;gap:6px;">
          <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('departmentId', ${d.id})">${d.employeeCount} staff</button>
          <button class="btn btn-outline btn-xs" onclick="openEditDepartment(${d.id})">Edit</button>
        </div>
      </div>
      ${buildNodes(d.id)}
    </div>
  `).join('') + `</div>`;
}

function openCreateDepartment() {
  document.getElementById('deptModalTitle').textContent = 'New Department';
  document.getElementById('departmentForm').reset();
  document.getElementById('deptId').value = '';
  document.getElementById('btnDeleteDepartment').classList.add('hidden');
  openModal('departmentModal');
}

function openEditDepartment(id) {
  const d = state.departments.find(x => x.id === id);
  if (!d) return;
  document.getElementById('deptModalTitle').textContent = `Edit Department — ${d.name}`;
  document.getElementById('deptId').value = d.id;
  document.getElementById('deptName').value = d.name || '';
  document.getElementById('deptShortName').value = d.shortName || '';
  document.getElementById('deptParentId').value = d.parentDepartmentId || '';
  document.getElementById('deptStructuredName').value = d.structuredName || '';
  document.getElementById('deptFunctionalShortcut').value = d.functionalShortcut || '';
  document.getElementById('deptManager').value = d.managerUsername || '';
  document.getElementById('deptDeputy').value = d.deputyManagerUsername || '';
  document.getElementById('deptAdContainer').value = d.adContainerForNewHires || '';
  document.getElementById('btnDeleteDepartment').classList.remove('hidden');
  openModal('departmentModal');
}

// ============================================================================
// 3. COST CENTERS
// ============================================================================
function bindCostCenterEvents() {
  document.getElementById('ccSearch').addEventListener('input', renderCostCenters);
  document.getElementById('btnAddCostCenter').addEventListener('click', openCreateCostCenter);

  document.getElementById('btnCcTableView').addEventListener('click', () => {
    document.getElementById('btnCcTableView').classList.add('active');
    document.getElementById('btnCcTreeView').classList.remove('active');
    document.getElementById('ccTableContainer').classList.remove('hidden');
    document.getElementById('ccTreeContainer').classList.add('hidden');
  });

  document.getElementById('btnCcTreeView').addEventListener('click', () => {
    document.getElementById('btnCcTreeView').classList.add('active');
    document.getElementById('btnCcTableView').classList.remove('active');
    document.getElementById('ccTreeContainer').classList.remove('hidden');
    document.getElementById('ccTableContainer').classList.add('hidden');
  });

  document.getElementById('costCenterForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = document.getElementById('ccId').value;
    const payload = {
      code: document.getElementById('ccCode').value,
      shortName: document.getElementById('ccShortName').value || null,
      parentCostCenterId: parseInt(document.getElementById('ccParentId').value, 10) || null,
      structuredName: document.getElementById('ccStructuredName').value || null,
      companyMainStructure: document.getElementById('ccMainStruct').value || null,
      companyCountryStructure: document.getElementById('ccCountryStruct').value || null,
      managerUsername: document.getElementById('ccManager').value || null
    };
    try {
      if (id) {
        await api(`api/costcenters/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
        showToast('Cost Center updated.');
      } else {
        await api('api/costcenters', { method: 'POST', body: JSON.stringify(payload) });
        showToast('Cost Center created.');
      }
      closeModal('costCenterModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnDeleteCostCenter').addEventListener('click', async () => {
    const id = document.getElementById('ccId').value;
    const code = document.getElementById('ccStructuredName').value;
    if (!id || !confirm(`Delete Cost Center "${code}"?`)) return;
    try {
      await api(`api/costcenters/${id}`, { method: 'DELETE' });
      showToast('Cost Center deleted.');
      closeModal('costCenterModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });
}

function renderCostCenters() {
  const term = (document.getElementById('ccSearch').value || '').trim().toLowerCase();
  const filtered = state.costCenters.filter(c =>
    !term ||
    (c.code && c.code.toLowerCase().includes(term)) ||
    (c.structuredName && c.structuredName.toLowerCase().includes(term)) ||
    (c.shortName && c.shortName.toLowerCase().includes(term)) ||
    (c.companyMainStructure && c.companyMainStructure.toLowerCase().includes(term)) ||
    (c.companyCountryStructure && c.companyCountryStructure.toLowerCase().includes(term)) ||
    (c.managerUsername && c.managerUsername.toLowerCase().includes(term))
  );

  const tbody = document.getElementById('ccTableBody');
  tbody.innerHTML = filtered.map(c => `
    <tr>
      <td class="mono"><strong>${esc(c.code)}</strong></td>
      <td class="mono">${esc(c.structuredName)}</td>
      <td>${esc(c.shortName || '—')}</td>
      <td>${esc(c.companyMainStructure || '—')}</td>
      <td>${c.companyCountryStructure ? `<span class="country-pill">${esc(c.companyCountryStructure)}</span>` : '—'}</td>
      <td><span class="country-pill">L${c.treeLevel}</span></td>
      <td class="mono">${esc(c.managerUsername || '—')}</td>
      <td>
        <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('costCenterId', ${c.id})">
          ${c.employeeCount} staff
        </button>
      </td>
      <td class="text-right">
        <button class="btn btn-outline btn-xs" onclick="openEditCostCenter(${c.id})">Edit</button>
      </td>
    </tr>
  `).join('');

  // Render Tree
  const treeContainer = document.getElementById('ccTreeContainer');
  const byParent = new Map();
  filtered.forEach(c => {
    const pid = c.parentCostCenterId || 0;
    if (!byParent.has(pid)) byParent.set(pid, []);
    byParent.get(pid).push(c);
  });

  function buildCcNodes(parentId) {
    const children = byParent.get(parentId) || [];
    if (children.length === 0) return '';
    return children.map(c => `
      <div class="tree-node">
        <div class="tree-item">
          <div>
            <span class="mono"><strong>${esc(c.structuredName)}</strong></span> — ${esc(c.shortName || c.code)}
            ${c.managerUsername ? `<span class="country-pill" style="margin-left:8px;">Mgr: ${esc(c.managerUsername)}</span>` : ''}
          </div>
          <div style="display:flex;gap:6px;">
            <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('costCenterId', ${c.id})">${c.employeeCount} staff</button>
            <button class="btn btn-outline btn-xs" onclick="openEditCostCenter(${c.id})">Edit</button>
          </div>
        </div>
        ${buildCcNodes(c.id)}
      </div>
    `).join('');
  }

  const rootIds = new Set(filtered.map(c => c.id));
  const topNodes = filtered.filter(c => !c.parentCostCenterId || !rootIds.has(c.parentCostCenterId));
  treeContainer.innerHTML = `<div class="tree-root">` + topNodes.map(c => `
    <div class="tree-node">
      <div class="tree-item">
        <div>
          <span class="mono"><strong>${esc(c.structuredName)}</strong></span> — ${esc(c.shortName || c.code)}
          ${c.managerUsername ? `<span class="country-pill" style="margin-left:8px;">Mgr: ${esc(c.managerUsername)}</span>` : ''}
        </div>
        <div style="display:flex;gap:6px;">
          <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('costCenterId', ${c.id})">${c.employeeCount} staff</button>
          <button class="btn btn-outline btn-xs" onclick="openEditCostCenter(${c.id})">Edit</button>
        </div>
      </div>
      ${buildCcNodes(c.id)}
    </div>
  `).join('') + `</div>`;
}

function openCreateCostCenter() {
  document.getElementById('ccModalTitle').textContent = 'New Cost Center';
  document.getElementById('costCenterForm').reset();
  document.getElementById('ccId').value = '';
  document.getElementById('btnDeleteCostCenter').classList.add('hidden');
  openModal('costCenterModal');
}

function openEditCostCenter(id) {
  const c = state.costCenters.find(x => x.id === id);
  if (!c) return;
  document.getElementById('ccModalTitle').textContent = `Edit Cost Center — ${c.structuredName}`;
  document.getElementById('ccId').value = c.id;
  document.getElementById('ccCode').value = c.code || '';
  document.getElementById('ccShortName').value = c.shortName || '';
  document.getElementById('ccParentId').value = c.parentCostCenterId || '';
  document.getElementById('ccStructuredName').value = c.structuredName || '';
  document.getElementById('ccMainStruct').value = c.companyMainStructure || '';
  document.getElementById('ccCountryStruct').value = c.companyCountryStructure || '';
  document.getElementById('ccManager').value = c.managerUsername || '';
  document.getElementById('btnDeleteCostCenter').classList.remove('hidden');
  openModal('costCenterModal');
}

// ============================================================================
// 4. LOCATIONS
// ============================================================================
function bindLocationEvents() {
  document.getElementById('locSearch').addEventListener('input', renderLocations);
  document.getElementById('locCountryFilter').addEventListener('change', renderLocations);
  document.getElementById('btnAddLocation').addEventListener('click', openCreateLocation);

  document.getElementById('locationForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = document.getElementById('locId').value;
    const payload = {
      countryShortcut: document.getElementById('locCountry').value,
      city: document.getElementById('locCity').value,
      zipCode: document.getElementById('locZip').value || null,
      street: document.getElementById('locStreet').value,
      name: document.getElementById('locName').value || null,
      structuredName: document.getElementById('locStructuredName').value || null,
      description: document.getElementById('locDescription').value || null,
      phone: document.getElementById('locPhone').value || null,
      fax: document.getElementById('locFax').value || null,
      managerUsername: document.getElementById('locManager').value || null,
      assistantManagerUsername: document.getElementById('locAssistant').value || null
    };
    try {
      if (id) {
        await api(`api/locations/${id}`, { method: 'PUT', body: JSON.stringify(payload) });
        showToast('Location updated.');
      } else {
        await api('api/locations', { method: 'POST', body: JSON.stringify(payload) });
        showToast('Location created.');
      }
      closeModal('locationModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });

  document.getElementById('btnDeleteLocation').addEventListener('click', async () => {
    const id = document.getElementById('locId').value;
    const name = document.getElementById('locStructuredName').value;
    if (!id || !confirm(`Delete Location "${name}"?`)) return;
    try {
      await api(`api/locations/${id}`, { method: 'DELETE' });
      showToast('Location deleted.');
      closeModal('locationModal');
      await refreshAllReferenceData();
    } catch (err) {
      showToast(err.message, 'error');
    }
  });
}

function renderLocations() {
  const term = (document.getElementById('locSearch').value || '').trim().toLowerCase();
  const country = document.getElementById('locCountryFilter').value;

  const filtered = state.locations.filter(l => {
    if (country && l.countryShortcut !== country) return false;
    if (!term) return true;
    return (
      (l.name && l.name.toLowerCase().includes(term)) ||
      (l.structuredName && l.structuredName.toLowerCase().includes(term)) ||
      (l.city && l.city.toLowerCase().includes(term)) ||
      (l.street && l.street.toLowerCase().includes(term)) ||
      (l.zipCode && l.zipCode.toLowerCase().includes(term)) ||
      (l.managerUsername && l.managerUsername.toLowerCase().includes(term))
    );
  });

  const tbody = document.getElementById('locTableBody');
  tbody.innerHTML = filtered.map(l => `
    <tr>
      <td><span class="country-pill">${esc(l.countryShortcut || '—')}</span></td>
      <td><strong>${esc(l.city || '—')}</strong></td>
      <td>
        ${esc(l.street || '—')}
        <span class="subtext mono">${l.zipCode ? 'ZIP: ' + esc(l.zipCode) : ''}</span>
      </td>
      <td>
        <span>${esc(l.structuredName)}</span>
        <span class="subtext">${esc(l.description || '')}</span>
      </td>
      <td class="mono">
        ${esc(l.phone || '—')}
        ${l.fax ? `<span class="subtext">Fax: ${esc(l.fax)}</span>` : ''}
      </td>
      <td class="mono">
        Mgr: ${esc(l.managerUsername || '—')}
        ${l.assistantManagerUsername ? `<span class="subtext">Asst: ${esc(l.assistantManagerUsername)}</span>` : ''}
      </td>
      <td>
        <button class="btn btn-outline btn-xs" onclick="filterEmployeesBy('locationId', ${l.id})">
          ${l.employeeCount} staff
        </button>
      </td>
      <td class="text-right">
        <button class="btn btn-outline btn-xs" onclick="openEditLocation(${l.id})">Edit</button>
      </td>
    </tr>
  `).join('');
}

function openCreateLocation() {
  document.getElementById('locModalTitle').textContent = 'New Location';
  document.getElementById('locationForm').reset();
  document.getElementById('locId').value = '';
  document.getElementById('btnDeleteLocation').classList.add('hidden');
  openModal('locationModal');
}

function openEditLocation(id) {
  const l = state.locations.find(x => x.id === id);
  if (!l) return;
  document.getElementById('locModalTitle').textContent = `Edit Location — ${l.city || l.name}`;
  document.getElementById('locId').value = l.id;
  document.getElementById('locCountry').value = l.countryShortcut || '';
  document.getElementById('locCity').value = l.city || '';
  document.getElementById('locZip').value = l.zipCode || '';
  document.getElementById('locStreet').value = l.street || '';
  document.getElementById('locName').value = l.name || '';
  document.getElementById('locStructuredName').value = l.structuredName || '';
  document.getElementById('locDescription').value = l.description || '';
  document.getElementById('locPhone').value = l.phone || '';
  document.getElementById('locFax').value = l.fax || '';
  document.getElementById('locManager').value = l.managerUsername || '';
  document.getElementById('locAssistant').value = l.assistantManagerUsername || '';
  document.getElementById('btnDeleteLocation').classList.remove('hidden');
  openModal('locationModal');
}

