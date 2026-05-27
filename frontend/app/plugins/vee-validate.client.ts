import { defineRule } from "vee-validate";
import { required, email, min } from "@vee-validate/rules";

export default defineNuxtPlugin((_nuxtApp) => {
  defineRule("required", required);
  defineRule("email", email);
  defineRule("min", min);
  defineRule("confirmed", (value: any, [target]: any) => {
    if (value === target) {
      return true;
    }
    return "Passwords do not match";
  });
  defineRule("json", (value: string) => {
    if (!value) return true;
    try {
      JSON.parse(value);
      return true;
    } catch {
      return "Invalid JSON format";
    }
  });
  defineRule("numeric", (value: string) => {
    if (!value) return true;
    if (/^\d+$/.test(value)) return true;
    return "Must be a number";
  });
});
