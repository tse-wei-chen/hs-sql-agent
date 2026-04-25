import { defineRule } from "vee-validate";
import { required, email, min } from "@vee-validate/rules";

export default defineNuxtPlugin((nuxtApp) => {
  defineRule("required", required);
  defineRule("email", email);
  defineRule("min", min);
  defineRule("confirmed", (value: any, [target]: any) => {
    if (value === target) {
      return true;
    }
    return "Passwords do not match";
  });
});
