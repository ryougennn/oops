using System.Runtime.InteropServices;
using static oopshell.Native.Vulkan;

namespace oopshell.Vulkan;

/// <summary>
/// One Vulkan instance, physical device, logical device and graphics queue that every <see cref="VulkanWindow"/>
/// of an app uses. Created on first use and destroyed with the app.
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    private static readonly Dictionary<OopsApp, VulkanDevice> s_devices = new();

    /// <summary>Instance extensions that are enabled (pass them on to e.g. Skia's <c>GRVkExtensions</c>).</summary>
    public static readonly string[] InstanceExtensions = ["VK_KHR_surface", "VK_KHR_wayland_surface"];
    /// <summary>Device extensions that are enabled.</summary>
    public static readonly string[] DeviceExtensions = ["VK_KHR_swapchain"];

    // All-false VkPhysicalDeviceFeatures: passed to vkCreateDevice and exposed as EnabledFeatures.
    private readonly nint _features;
    private bool _disposed;

    private VulkanDevice(OopsApp app)
    {
        if (!IsAvailable)
            throw new NotSupportedException("libvulkan.so.1 is not installed.");
        App = app;
        uint loaderVersion;
        ApiVersion = vkEnumerateInstanceVersion(&loaderVersion) == VK_SUCCESS ? Math.Max(loaderVersion, VK_API_VERSION_1_1) : VK_API_VERSION_1_1;
        _features = (nint)NativeMemory.AllocZeroed(PhysicalDeviceFeaturesSize);
        try
        {
            Instance = CreateInstance(ApiVersion);
            (PhysicalDevice, QueueFamily, DeviceName, uint deviceApi) = PickPhysicalDevice(Instance, app.Display.Handle);
            ApiVersion = Math.Min(ApiVersion, deviceApi);
            Device = CreateDevice(PhysicalDevice, QueueFamily, (void*)_features);
            nint queue;
            vkGetDeviceQueue(Device, QueueFamily, 0, &queue);
            Queue = queue;
        }
        catch
        {
            if (Instance != 0)
                vkDestroyInstance(Instance, 0);
            NativeMemory.Free((void*)_features);
            throw;
        }
    }

    /// <summary>The app's Vulkan device, created on first use. Throws <see cref="NotSupportedException"/> without Vulkan.</summary>
    public static VulkanDevice Get(OopsApp app)
    {
        if (!s_devices.TryGetValue(app, out var device))
        {
            s_devices[app] = device = new VulkanDevice(app);
            app.Disposing += device.Dispose;
        }
        return device;
    }

    public OopsApp App { get; }
    /// <summary>The API version both the loader and the device support (at least 1.1).</summary>
    public uint ApiVersion { get; }
    public nint Instance { get; }
    public nint PhysicalDevice { get; }
    public nint Device { get; }
    public nint Queue { get; }
    public uint QueueFamily { get; }
    public string DeviceName { get; }
    /// <summary>A <c>VkPhysicalDeviceFeatures*</c> describing the enabled features (none).</summary>
    public nint EnabledFeatures => _features;

    /// <summary>Raised before the device is destroyed (e.g. so a GPU drawing library can drop its context).</summary>
    public event Action? Disposing;

    /// <summary>Looks up an instance-level (<paramref name="device"/> = 0) or device-level function.</summary>
    public static nint GetProcAddress(string name, nint instance, nint device) =>
        device != 0 ? vkGetDeviceProcAddr(device, name) : vkGetInstanceProcAddr(instance, name);

    internal static void Check(int result, string call)
    {
        if (result != VK_SUCCESS)
            throw new InvalidOperationException($"{call} failed (VkResult {result}).");
    }

    private static nint CreateInstance(uint apiVersion)
    {
        var names = new Utf8Strings(InstanceExtensions);
        try
        {
            var appInfo = new VkApplicationInfo { sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, apiVersion = apiVersion };
            var info = new VkInstanceCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = &appInfo,
                enabledExtensionCount = (uint)InstanceExtensions.Length,
                ppEnabledExtensionNames = names.Pointers,
            };
            nint instance;
            Check(vkCreateInstance(&info, 0, &instance), "vkCreateInstance");
            return instance;
        }
        finally
        {
            names.Free();
        }
    }

    /// <summary>
    /// The first device in the loader's order (Mesa's device-select layer puts the GPU driving the display first)
    /// with a graphics queue that can present to this Wayland display. CPU devices (lavapipe) only as a last resort.
    /// </summary>
    private static (nint Device, uint Family, string Name, uint ApiVersion) PickPhysicalDevice(nint instance, nint wlDisplay)
    {
        uint count;
        Check(vkEnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new nint[count];
        fixed (nint* p = devices)
            Check(vkEnumeratePhysicalDevices(instance, &count, p), "vkEnumeratePhysicalDevices");

        (nint, uint, string, uint)? cpuFallback = null;
        byte* properties = stackalloc byte[PhysicalDevicePropertiesSize];
        foreach (var device in devices)
        {
            vkGetPhysicalDeviceProperties(device, properties);
            uint api = *(uint*)(properties + PropertiesApiVersionOffset);
            int type = *(int*)(properties + PropertiesDeviceTypeOffset);
            string name = Marshal.PtrToStringUTF8((nint)(properties + PropertiesDeviceNameOffset)) ?? "?";
            if (api < VK_API_VERSION_1_1 || FindQueueFamily(device, wlDisplay) is not { } family)
                continue;
            if (type != VK_PHYSICAL_DEVICE_TYPE_CPU)
                return (device, family, name, api);
            cpuFallback ??= (device, family, name, api);
        }
        return cpuFallback ?? throw new NotSupportedException("No Vulkan device can present to this Wayland display.");
    }

    private static uint? FindQueueFamily(nint device, nint wlDisplay)
    {
        uint count;
        vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0 && vkGetPhysicalDeviceWaylandPresentationSupportKHR(device, i, wlDisplay) != 0)
                return i;
        }
        return null;
    }

    private static nint CreateDevice(nint physicalDevice, uint family, void* features)
    {
        var names = new Utf8Strings(DeviceExtensions);
        try
        {
            float priority = 1;
            var queueInfo = new VkDeviceQueueCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = family,
                queueCount = 1,
                pQueuePriorities = &priority,
            };
            var info = new VkDeviceCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                queueCreateInfoCount = 1,
                pQueueCreateInfos = &queueInfo,
                enabledExtensionCount = (uint)DeviceExtensions.Length,
                ppEnabledExtensionNames = names.Pointers,
                pEnabledFeatures = features,
            };
            nint device;
            Check(vkCreateDevice(physicalDevice, &info, 0, &device), "vkCreateDevice");
            return device;
        }
        finally
        {
            names.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disposing?.Invoke();
        App.Disposing -= Dispose;
        s_devices.Remove(App);
        vkDeviceWaitIdle(Device);
        vkDestroyDevice(Device, 0);
        vkDestroyInstance(Instance, 0);
        NativeMemory.Free((void*)_features);
    }

    /// <summary>A native array of NUL-terminated UTF-8 strings, for <c>ppEnabledExtensionNames</c>.</summary>
    private readonly struct Utf8Strings(string[] strings)
    {
        public byte** Pointers { get; } = Allocate(strings);

        private static byte** Allocate(string[] strings)
        {
            var array = (byte**)NativeMemory.Alloc((nuint)strings.Length, (nuint)sizeof(byte*));
            for (int i = 0; i < strings.Length; i++)
                array[i] = (byte*)Marshal.StringToCoTaskMemUTF8(strings[i]);
            return array;
        }

        public void Free()
        {
            for (int i = 0; i < strings.Length; i++)
                Marshal.FreeCoTaskMem((nint)Pointers[i]);
            NativeMemory.Free(Pointers);
        }
    }
}
