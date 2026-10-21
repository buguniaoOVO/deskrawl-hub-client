#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <ctype.h>

#define FRAME_CAP_MESSAGE (WM_APP + 0x5B)
#define STATUS_PREFIX L"frame-cap-live-status-"
#define MAX_CONFIG_BYTES 4096

typedef void *(__cdecl *domain_get_fn)(void);
typedef void *(__cdecl *thread_attach_fn)(void *domain);
typedef const void **(__cdecl *domain_get_assemblies_fn)(void *domain, size_t *count);
typedef void *(__cdecl *assembly_get_image_fn)(const void *assembly);
typedef const char *(__cdecl *image_get_name_fn)(void *image);
typedef void *(__cdecl *class_from_name_fn)(void *image, const char *namesp, const char *name);
typedef void *(__cdecl *class_get_method_fn)(void *klass, const char *name, int parameter_count);
typedef void *(__cdecl *runtime_invoke_fn)(void *method, void *instance, void **parameters, void **exception);
typedef void *(__cdecl *object_unbox_fn)(void *object);

static HWND g_window;
static WNDPROC g_original_window_proc;
static volatile LONG g_hook_state;
static wchar_t g_config_path[MAX_PATH * 4];
static wchar_t g_status_path[MAX_PATH * 4];
static char g_request_id[128];
static char g_api_error[192];
static domain_get_fn g_domain_get;
static thread_attach_fn g_thread_attach;
static domain_get_assemblies_fn g_domain_get_assemblies;
static assembly_get_image_fn g_assembly_get_image;
static image_get_name_fn g_image_get_name;
static class_from_name_fn g_class_from_name;
static class_get_method_fn g_class_get_method;
static runtime_invoke_fn g_runtime_invoke;
static object_unbox_fn g_object_unbox;
static void *g_get_target_frame_rate;
static void *g_set_target_frame_rate;
static void *g_get_vsync_count;
static void *g_set_vsync_count;
static BOOL g_api_ready;
static BOOL g_enabled;
static BOOL g_original_captured;
static BOOL g_was_applied;
static BOOL g_has_original_vsync;
static int g_requested_fps = 10;
static int g_original_target_frame_rate;
static int g_original_vsync_count = 1;
static int g_applied_fps;

static BOOL CALLBACK FindUnityWindow(HWND window, LPARAM parameter)
{
    DWORD process_id = 0;
    WCHAR class_name[128] = {0};
    (void)parameter;

    if (GetWindowThreadProcessId(window, &process_id) == 0 || process_id != GetCurrentProcessId())
        return TRUE;
    if (GetClassNameW(window, class_name, (int)(sizeof(class_name) / sizeof(class_name[0]))) <= 0)
        return TRUE;
    if (wcscmp(class_name, L"UnityWndClass") != 0)
        return TRUE;

    g_window = window;
    return FALSE;
}

static void SetStatusPath(const wchar_t *config_path)
{
    wchar_t directory[MAX_PATH * 4] = {0};
    wchar_t *separator;

    wcsncpy_s(g_config_path, _countof(g_config_path), config_path, _TRUNCATE);
    wcsncpy_s(directory, _countof(directory), config_path, _TRUNCATE);
    separator = wcsrchr(directory, L'\\');
    if (separator == NULL)
        separator = wcsrchr(directory, L'/');
    if (separator == NULL)
    {
        g_status_path[0] = L'\0';
        return;
    }
    *separator = L'\0';
    swprintf_s(g_status_path, _countof(g_status_path), L"%ls\\%ls%lu.json",
        directory, STATUS_PREFIX, GetCurrentProcessId());
}

static void EscapeJson(const char *source, char *destination, size_t capacity)
{
    size_t output = 0;
    if (capacity == 0) return;
    for (size_t i = 0; source != NULL && source[i] != '\0' && output + 2 < capacity; i++)
    {
        unsigned char ch = (unsigned char)source[i];
        if (ch == '"' || ch == '\\')
        {
            destination[output++] = '\\';
            destination[output++] = (char)ch;
        }
        else if (ch >= 0x20)
        {
            destination[output++] = (char)ch;
        }
    }
    destination[output] = '\0';
}

static void WriteStatus(const char *state, const char *message, BOOL enabled, int fps)
{
    char json[1024];
    char escaped_message[384];
    char escaped_request[160];
    wchar_t temporary_path[MAX_PATH * 4];
    HANDLE file;
    DWORD written = 0;
    int json_length;
    FILETIME now;
    ULARGE_INTEGER ticks;
    unsigned long long unix_time;

    if (g_status_path[0] == L'\0') return;
    GetSystemTimeAsFileTime(&now);
    ticks.LowPart = now.dwLowDateTime;
    ticks.HighPart = now.dwHighDateTime;
    unix_time = ticks.QuadPart / 10000000ULL - 11644473600ULL;
    EscapeJson(message, escaped_message, sizeof(escaped_message));
    EscapeJson(g_request_id, escaped_request, sizeof(escaped_request));
    json_length = snprintf(json, sizeof(json),
        "{\"pid\":%lu,\"state\":\"%s\",\"enabled\":%s,\"fps\":%d,\"message\":\"%s\",\"requestId\":\"%s\",\"updatedAtUtc\":%llu}",
        GetCurrentProcessId(), state, enabled ? "true" : "false", fps,
        escaped_message, escaped_request,
        unix_time);
    if (json_length <= 0 || (size_t)json_length >= sizeof(json)) return;

    swprintf_s(temporary_path, _countof(temporary_path), L"%s.tmp", g_status_path);
    file = CreateFileW(temporary_path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    WriteFile(file, json, (DWORD)json_length, &written, NULL);
    CloseHandle(file);
    if (written == (DWORD)json_length)
        MoveFileExW(temporary_path, g_status_path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

static char *FindJsonValue(char *json, const char *key)
{
    char needle[64];
    int length = snprintf(needle, sizeof(needle), "\"%s\"", key);
    if (length <= 0 || (size_t)length >= sizeof(needle)) return NULL;
    char *cursor = strstr(json, needle);
    if (cursor == NULL) return NULL;
    cursor = strchr(cursor + length, ':');
    if (cursor == NULL) return NULL;
    cursor++;
    while (*cursor != '\0' && isspace((unsigned char)*cursor)) cursor++;
    return cursor;
}

static void ReadSettings(void)
{
    char json[MAX_CONFIG_BYTES + 1] = {0};
    FILE *file = NULL;
    size_t length;

    g_enabled = FALSE;
    g_requested_fps = 10;
    g_request_id[0] = '\0';
    if (_wfopen_s(&file, g_config_path, L"rb") != 0 || file == NULL) return;
    length = fread(json, 1, MAX_CONFIG_BYTES, file);
    fclose(file);
    json[length] = '\0';

    char *value = FindJsonValue(json, "enabled");
    if (value != NULL) g_enabled = strncmp(value, "true", 4) == 0;
    value = FindJsonValue(json, "fps");
    if (value != NULL)
    {
        int fps = atoi(value);
        if (fps >= 1 && fps <= 240) g_requested_fps = fps;
    }
    value = FindJsonValue(json, "requestId");
    if (value != NULL && *value == '"')
    {
        value++;
        char *end = strchr(value, '"');
        if (end != NULL)
        {
            size_t id_length = (size_t)(end - value);
            if (id_length >= sizeof(g_request_id)) id_length = sizeof(g_request_id) - 1;
            memcpy(g_request_id, value, id_length);
            g_request_id[id_length] = '\0';
        }
    }
}

static BOOL BindApi(void)
{
    HMODULE game_assembly = GetModuleHandleW(L"GameAssembly.dll");
    if (game_assembly == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏正在启动。", _TRUNCATE);
        return FALSE;
    }

#define BIND_API(field, symbol) \
    field = (void *)GetProcAddress(game_assembly, symbol); \
    if (field == NULL) { strncpy_s(g_api_error, sizeof(g_api_error), "游戏帧率接口不可用。", _TRUNCATE); return FALSE; }

    BIND_API(g_domain_get, "il2cpp_domain_get");
    BIND_API(g_thread_attach, "il2cpp_thread_attach");
    BIND_API(g_domain_get_assemblies, "il2cpp_domain_get_assemblies");
    BIND_API(g_assembly_get_image, "il2cpp_assembly_get_image");
    BIND_API(g_image_get_name, "il2cpp_image_get_name");
    BIND_API(g_class_from_name, "il2cpp_class_from_name");
    BIND_API(g_class_get_method, "il2cpp_class_get_method_from_name");
    BIND_API(g_runtime_invoke, "il2cpp_runtime_invoke");
    BIND_API(g_object_unbox, "il2cpp_object_unbox");
#undef BIND_API

    void *domain = g_domain_get();
    if (domain == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏运行组件还未就绪。", _TRUNCATE);
        return FALSE;
    }
    if (g_thread_attach(domain) == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏线程还未就绪。", _TRUNCATE);
        return FALSE;
    }

    size_t assembly_count = 0;
    const void **assemblies = g_domain_get_assemblies(domain, &assembly_count);
    if (assemblies == NULL || assembly_count == 0 || assembly_count > 4096)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏运行组件还未就绪。", _TRUNCATE);
        return FALSE;
    }

    void *core_image = NULL;
    for (size_t i = 0; i < assembly_count; i++)
    {
        void *image = g_assembly_get_image(assemblies[i]);
        if (image == NULL) continue;
        const char *image_name = g_image_get_name(image);
        if (image_name != NULL &&
            (_stricmp(image_name, "UnityEngine.CoreModule.dll") == 0 ||
             _stricmp(image_name, "UnityEngine.CoreModule") == 0))
        {
            core_image = image;
            break;
        }
    }
    if (core_image == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏帧率接口还未就绪。", _TRUNCATE);
        return FALSE;
    }

    void *application = g_class_from_name(core_image, "UnityEngine", "Application");
    void *quality = g_class_from_name(core_image, "UnityEngine", "QualitySettings");
    if (application == NULL || quality == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏帧率接口还未就绪。", _TRUNCATE);
        return FALSE;
    }

    g_get_target_frame_rate = g_class_get_method(application, "get_targetFrameRate", 0);
    g_set_target_frame_rate = g_class_get_method(application, "set_targetFrameRate", 1);
    g_get_vsync_count = g_class_get_method(quality, "get_vSyncCount", 0);
    g_set_vsync_count = g_class_get_method(quality, "set_vSyncCount", 1);
    if (g_get_target_frame_rate == NULL || g_set_target_frame_rate == NULL || g_set_vsync_count == NULL)
    {
        strncpy_s(g_api_error, sizeof(g_api_error), "游戏帧率接口不可用。", _TRUNCATE);
        return FALSE;
    }

    g_api_ready = TRUE;
    g_api_error[0] = '\0';
    return TRUE;
}

static BOOL InvokeIntGetter(void *method, int *value)
{
    void *exception = NULL;
    void *boxed = g_runtime_invoke(method, NULL, NULL, &exception);
    if (exception != NULL || boxed == NULL) return FALSE;
    int *unboxed = (int *)g_object_unbox(boxed);
    if (unboxed == NULL) return FALSE;
    *value = *unboxed;
    return TRUE;
}

static BOOL InvokeIntSetter(void *method, int value)
{
    void *exception = NULL;
    void *argument = &value;
    void *arguments[1] = { argument };
    g_runtime_invoke(method, NULL, arguments, &exception);
    return exception == NULL;
}

static void ApplySettingsOnGameThread(void)
{
    ReadSettings();
    if (!g_enabled && !g_was_applied)
    {
        WriteStatus("disabled", "限帧已关闭。", FALSE, 0);
        return;
    }

    if (!g_api_ready && !BindApi())
    {
        WriteStatus("waiting", g_api_error, g_enabled, g_applied_fps);
        return;
    }

    if (!g_enabled)
    {
        if (g_original_captured)
        {
            int vsync = g_has_original_vsync ? g_original_vsync_count : 1;
            if (!InvokeIntSetter(g_set_vsync_count, vsync)
                || !InvokeIntSetter(g_set_target_frame_rate, g_original_target_frame_rate))
            {
                WriteStatus("error", "游戏帧率恢复失败。", FALSE, 0);
                return;
            }
        }
        g_was_applied = FALSE;
        g_applied_fps = 0;
        WriteStatus("disabled", "限帧已关闭。", FALSE, 0);
        return;
    }

    if (!g_original_captured)
    {
        if (!InvokeIntGetter(g_get_target_frame_rate, &g_original_target_frame_rate))
        {
            WriteStatus("error", "无法读取游戏原有帧率。", TRUE, 0);
            return;
        }
        g_has_original_vsync = g_get_vsync_count != NULL
            && InvokeIntGetter(g_get_vsync_count, &g_original_vsync_count);
        if (!g_has_original_vsync) g_original_vsync_count = 1;
        g_original_captured = TRUE;
    }

    int current_target = 0;
    if (!InvokeIntGetter(g_get_target_frame_rate, &current_target)
        || !InvokeIntSetter(g_set_vsync_count, 0))
    {
        WriteStatus("error", "游戏帧率设置失败。", TRUE, 0);
        return;
    }
    if (!g_was_applied || current_target != g_requested_fps)
    {
        if (!InvokeIntSetter(g_set_target_frame_rate, g_requested_fps))
        {
            WriteStatus("error", "游戏帧率设置失败。", TRUE, 0);
            return;
        }
        g_was_applied = TRUE;
        g_applied_fps = g_requested_fps;
    }
    char status_message[96];
    snprintf(status_message, sizeof(status_message), "已应用 %d FPS 上限。", g_applied_fps);
    WriteStatus("applied", status_message, TRUE, g_applied_fps);
}

static LRESULT CALLBACK FrameCapWindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == FRAME_CAP_MESSAGE && window == g_window)
        ApplySettingsOnGameThread();

    if (g_original_window_proc != NULL)
        return CallWindowProcW(g_original_window_proc, window, message, wparam, lparam);
    return DefWindowProcW(window, message, wparam, lparam);
}

__declspec(dllexport) DWORD WINAPI DeskrawlFrameCapStart(const wchar_t *config_path)
{
    if (config_path == NULL || config_path[0] == L'\0') return ERROR_INVALID_PARAMETER;
    SetStatusPath(config_path);

    LONG state = InterlockedCompareExchange(&g_hook_state, 2, 0);
    if (state == 2) return ERROR_BUSY;
    if (state == 0 || !IsWindow(g_window))
    {
        if (state == 1 && InterlockedCompareExchange(&g_hook_state, 2, 1) != 1)
            return ERROR_BUSY;
        g_window = NULL;
        EnumWindows(FindUnityWindow, 0);
        if (g_window == NULL)
        {
            InterlockedExchange(&g_hook_state, 0);
            return ERROR_INVALID_WINDOW_HANDLE;
        }

        SetLastError(0);
        WNDPROC previous = (WNDPROC)SetWindowLongPtrW(g_window, GWLP_WNDPROC, (LONG_PTR)FrameCapWindowProc);
        DWORD error = GetLastError();
        if (previous == NULL && error != 0)
        {
            g_window = NULL;
            InterlockedExchange(&g_hook_state, 0);
            return error;
        }
        g_original_window_proc = previous;
        InterlockedExchange(&g_hook_state, 1);
    }
    else if (state == 2)
    {
        return ERROR_BUSY;
    }

    DWORD_PTR message_result = 0;
    if (!SendMessageTimeoutW(g_window, FRAME_CAP_MESSAGE, 0, 0,
        SMTO_ABORTIFHUNG, 2500, &message_result))
    {
        WriteStatus("waiting", "游戏窗口暂时没有响应。", g_enabled, g_applied_fps);
        return ERROR_TIMEOUT;
    }
    return ERROR_SUCCESS;
}
