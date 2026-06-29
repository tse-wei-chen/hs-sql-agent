import xior from "xior";
import { refreshToken, signOut } from "./auth";
import { toast } from "vue-sonner";

let refreshPromise: Promise<any> | null = null;

export const xiorInstance = xior.create({
  baseURL: "/api",
  timeout: 10000,
});

xiorInstance.interceptors.request.use((config) => {
  // You can add headers or modify the request config here
  return config;
});

xiorInstance.interceptors.response.use(
  (response) => {
    // You can handle responses globally here
    return response;
  },
  (error) => {
    // You can handle errors globally here
    return Promise.reject(error);
  },
);

export const xiorInstanceToken = xior.create({
  baseURL: "/api",
  timeout: 10000,
});

xiorInstanceToken.interceptors.request.use((config) => {
  config.headers["Authorization"] =
    `Bearer ${localStorage.getItem("accessToken")}`;
  return config;
});

xiorInstanceToken.interceptors.response.use(
  (response) => response,
  async (error: any) => {
    if (error.response?.status === 401) {
      if (!refreshPromise) {
        refreshPromise = refreshToken().finally(() => {
          refreshPromise = null;
        });
      }

      const response = await refreshPromise;

      if (!response?.accessToken) {
        return Promise.reject(error);
      }

      localStorage.setItem("accessToken", response.accessToken);
      localStorage.setItem("refreshToken", response.refreshToken);

      const updatedConfig = {
        ...error.config,
        headers: {
          ...error.config.headers,
          Authorization: `Bearer ${response.accessToken}`,
        },
      };

      return xior.request(updatedConfig);
    }
    if (error.response?.status === 403) {
      toast.error("Permission denied. You have been logged out.");
      await signOut();
      localStorage.removeItem("accessToken");
      localStorage.removeItem("refreshToken");
      localStorage.removeItem("permissions");
      localStorage.removeItem("userEmail");
      localStorage.removeItem("userName");
      await navigateTo("/login");
    }
    return Promise.reject(error);
  },
);

export const xiorInstanceRefreshToken = xior.create({
  baseURL: "/api",
  timeout: 10000,
});

xiorInstanceRefreshToken.interceptors.request.use((config) => {
  config.headers["Authorization"] =
    `Bearer ${localStorage.getItem("refreshToken")}`;
  return config;
});

xiorInstanceRefreshToken.interceptors.response.use(
  (response) => {
    // You can handle responses globally here
    return response;
  },
  async (error) => {
    toast.error("Permission denied. You have been logged out.");
    await signOut();
    localStorage.removeItem("accessToken");
    localStorage.removeItem("refreshToken");
    localStorage.removeItem("permissions");
    localStorage.removeItem("userEmail");
    localStorage.removeItem("userName");
    await navigateTo("/login");
    return Promise.reject(error);
  },
);
