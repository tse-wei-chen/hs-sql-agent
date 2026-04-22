import xior from "xior";
import { refreshToken } from "./admin";

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
      console.log("🔄 Token expired, refreshing...");

      if (!refreshPromise) {
        refreshPromise = refreshToken().finally(() => {
          refreshPromise = null;
        });
      }

      const response = await refreshPromise;

      if (!response?.accessToken) {
        console.error("❌ Failed to refresh token");
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
      console.warn("⚠️ Forbidden (403), signing out...");
      localStorage.removeItem("accessToken");
      localStorage.removeItem("refreshToken");
      navigateTo("/login");
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
  (error) => {
    localStorage.removeItem("accessToken");
    localStorage.removeItem("refreshToken");
    navigateTo("/login");
    return Promise.reject(error);
  },
);
