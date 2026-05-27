import {
  xiorInstance,
  xiorInstanceRefreshToken,
} from "./xiorInstance";

export const checkFirstRun = async () => {
  try {
    const response = await xiorInstance.get("/admin/first-run");
    return response.data;
  } catch (error) {
    console.error("Error checking first run:", error);
    throw error;
  }
};

export const signIn = async (email: string, password: string) => {
  try {
    const response = await xiorInstance.post("/admin/sign-in", {
      email: email,
      password: password,
    });
    return response.data;
  } catch (error) {
    console.error("Error signing in:", error);
    throw error;
  }
};

export const signUp = async (email: string, password: string) => {
  try {
    const response = await xiorInstance.post("/admin/sign-up", {
      email: email,
      password: password,
    });
    return response.data;
  } catch (error) {
    console.error("Error signing up:", error);
    throw error;
  }
};

export const refreshToken = async () => {
  try {
    const response = await xiorInstanceRefreshToken.post(
      "/admin/refresh-token",
    );
    return response.data;
  } catch (error) {
    console.error("Error refreshing token:", error);
    throw error;
  }
};
