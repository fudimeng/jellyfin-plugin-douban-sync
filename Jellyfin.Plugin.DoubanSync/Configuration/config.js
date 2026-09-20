const apiBase = 'DoubanSync/';

function apiRequest(path, type, body) {
    const options = {
        type: type,
        url: ApiClient.getUrl(apiBase + path)
    };
    if (type === 'GET') {
        options.dataType = 'json';
    }
    if (body !== undefined) {
        options.data = JSON.stringify(body);
        options.contentType = 'application/json';
    }
    return ApiClient.ajax(options);
}

function escapeHtml(value) {
    const element = document.createElement('div');
    element.textContent = value || '';
    return element.innerHTML;
}

function selectedUser(view) {
    const id = view.querySelector('#jellyfinUser').value;
    return (view.doubanSyncState.Users || []).find(user => user.Id === id);
}

function renderCredentialStatus(view) {
    const user = selectedUser(view);
    const status = view.querySelector('#credentialStatus');
    const deleteButton = view.querySelector('#deleteCookie');
    if (!user || !user.CookieConfigured) {
        status.textContent = '尚未配置 Cookie。';
        deleteButton.classList.add('hide');
        return;
    }

    const dateText = user.CookieUpdatedAtUtc
        ? new Date(user.CookieUpdatedAtUtc).toLocaleString()
        : '未知时间';
    status.textContent = '已连接豆瓣账号：' + (user.DoubanDisplayName || '已登录') + '；更新于 ' + dateText + '。';
    deleteButton.classList.remove('hide');
}

function renderRecent(view, records) {
    const container = view.querySelector('#recentResults');
    if (!records || records.length === 0) {
        container.innerHTML = '<p class="fieldDescription">暂无同步记录。</p>';
        return;
    }

    let html = '<div class="paperList">';
    records.forEach(record => {
        const success = record.Status === 'success';
        const detail = success
            ? '豆瓣条目 ' + escapeHtml(record.DoubanId)
            : escapeHtml(record.Error);
        html += '<div class="listItem listItem-border">'
            + '<div class="listItemBody">'
            + '<div class="listItemBodyText">' + escapeHtml(record.Name) + '</div>'
            + '<div class="listItemBodyText secondary">'
            + (success ? '成功' : '失败') + ' · ' + detail
            + ' · ' + new Date(record.TimestampUtc).toLocaleString()
            + '</div></div></div>';
    });
    container.innerHTML = html + '</div>';
}

async function loadState(view) {
    Dashboard.showLoadingMsg();
    try {
        const state = await apiRequest('State', 'GET');
        view.doubanSyncState = state;
        view.querySelector('#enabled').checked = state.Enabled;
        view.querySelector('#markPrivate').checked = state.MarkPrivate;
        view.querySelector('#shareToBroadcast').checked = state.ShareToBroadcast;
        view.querySelector('#requestIntervalSeconds').value = state.RequestIntervalSeconds;
        view.querySelector('#barkStatus').textContent = state.BarkConfigured ? '已配置。留空可保留当前地址。' : '尚未配置。';
        view.querySelector('#discordWebhookStatus').textContent = state.DiscordWebhookConfigured ? '已配置。留空可保留当前地址。' : '尚未配置。';
        view.querySelector('#clearBark').checked = false;
        view.querySelector('#clearDiscordWebhook').checked = false;

        const previousUser = view.querySelector('#jellyfinUser').value;
        view.querySelector('#jellyfinUser').innerHTML = state.Users
            .map(user => '<option value="' + user.Id + '">' + escapeHtml(user.Name) + '</option>')
            .join('');
        if (state.Users.some(user => user.Id === previousUser)) {
            view.querySelector('#jellyfinUser').value = previousUser;
        }

        view.querySelector('#pendingCount').textContent = '待处理队列：' + state.PendingCount + ' 项';
        renderCredentialStatus(view);
        renderRecent(view, state.Recent);
    } finally {
        Dashboard.hideLoadingMsg();
    }
}

async function showRequestError(error, fallback) {
    let message = fallback;
    if (error && error.responseJSON && error.responseJSON.Message) {
        message = error.responseJSON.Message;
    } else if (error && typeof error.json === 'function') {
        try {
            const body = await error.json();
            message = body.Message || fallback;
        } catch {
            message = fallback;
        }
    }
    Dashboard.alert({ message: message });
}

export default function (view) {
    view.querySelector('#jellyfinUser').addEventListener('change', () => renderCredentialStatus(view));
    view.querySelector('#refreshState').addEventListener('click', () => loadState(view));

    view.querySelector('#doubanSyncSettingsForm').addEventListener('submit', async event => {
        event.preventDefault();
        Dashboard.showLoadingMsg();
        try {
            await apiRequest('Settings', 'POST', {
                Enabled: view.querySelector('#enabled').checked,
                MarkPrivate: view.querySelector('#markPrivate').checked,
                ShareToBroadcast: view.querySelector('#shareToBroadcast').checked,
                RequestIntervalSeconds: Number(view.querySelector('#requestIntervalSeconds').value)
            });
            Dashboard.alert({ message: '设置已保存。' });
            await loadState(view);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            await showRequestError(error, '保存设置失败。');
        }
        return false;
    });

    view.querySelector('#doubanSyncNotificationForm').addEventListener('submit', async event => {
        event.preventDefault();
        Dashboard.showLoadingMsg();
        try {
            await apiRequest('Notifications', 'POST', {
                BarkUrl: view.querySelector('#barkUrl').value.trim(),
                DiscordWebhookUrl: view.querySelector('#discordWebhookUrl').value.trim(),
                ClearBark: view.querySelector('#clearBark').checked,
                ClearDiscordWebhook: view.querySelector('#clearDiscordWebhook').checked
            });
            view.querySelector('#barkUrl').value = '';
            view.querySelector('#discordWebhookUrl').value = '';
            Dashboard.alert({ message: '通知设置已保存。' });
            await loadState(view);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            await showRequestError(error, '保存通知设置失败。');
        }
        return false;
    });

    view.querySelector('#doubanSyncCookieForm').addEventListener('submit', async event => {
        event.preventDefault();
        const userId = view.querySelector('#jellyfinUser').value;
        const cookie = view.querySelector('#cookie').value.trim();
        if (!userId || !cookie) {
            Dashboard.alert({ message: '请选择用户并粘贴 Cookie。' });
            return false;
        }

        Dashboard.showLoadingMsg();
        try {
            await apiRequest('Users/' + userId + '/Credentials', 'POST', { Cookie: cookie });
            view.querySelector('#cookie').value = '';
            Dashboard.alert({ message: 'Cookie 验证成功并已加密保存。' });
            await loadState(view);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            await showRequestError(error, 'Cookie 验证或保存失败。');
        }
        return false;
    });

    view.querySelector('#deleteCookie').addEventListener('click', async () => {
        const userId = view.querySelector('#jellyfinUser').value;
        Dashboard.showLoadingMsg();
        try {
            await apiRequest('Users/' + userId + '/Credentials', 'DELETE');
            await loadState(view);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            await showRequestError(error, '删除 Cookie 失败。');
        }
    });

    view.querySelector('#retryFailures').addEventListener('click', async () => {
        const userId = view.querySelector('#jellyfinUser').value;
        Dashboard.showLoadingMsg();
        try {
            await apiRequest('Users/' + userId + '/Retry', 'POST');
            await loadState(view);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            await showRequestError(error, '重新加入失败队列时出错。');
        }
    });

    view.addEventListener('viewshow', () => loadState(view));
}
