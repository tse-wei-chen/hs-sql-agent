import { xiorInstanceToken } from "./xiorInstance";

export interface DbSemantic {
  id: number;
  dbManagementId: number;
  schemaName: string;
  tableName: string;
  columnName?: string | null;
  description?: string | null;
  displayName?: string | null;
  synonyms: string[];
}

export interface DbSemanticRequest {
  dbManagementId: number;
  schemaName: string;
  tableName: string;
  columnName?: string | null;
  description?: string | null;
  displayName?: string | null;
  synonyms?: string[];
}

export interface DbSemanticRelationship {
  id: number;
  dbManagementId: number;
  name: string;
  sourceSchema?: string | null;
  sourceTable: string;
  sourceColumn: string;
  targetSchema?: string | null;
  targetTable: string;
  targetColumn: string;
  cardinality: "one-to-one" | "one-to-many" | "many-to-one" | "many-to-many";
  direction: "source-to-target" | "target-to-source" | "bidirectional";
  description?: string | null;
}

export interface DbSemanticMetric {
  id: number;
  dbManagementId: number;
  schemaName?: string | null;
  tableName: string;
  name: string;
  displayName?: string | null;
  description?: string | null;
  formula: string;
  aggregation: "sum" | "count" | "count-distinct" | "avg" | "min" | "max" | "custom";
  grain?: string | null;
  filter?: string | null;
  synonyms?: string[];
  executable: false;
}

export interface DbSemanticModel {
  dbManagementId: number;
  entities: DbSemantic[];
  relationships: DbSemanticRelationship[];
  metrics: DbSemanticMetric[];
}

export const getSemanticsByDbId = async (dbId: number) => {
  const response = await xiorInstanceToken.get<DbSemantic[]>(`/DbSemantic/${dbId}`);
  return response.data;
};

export const upsertSemantic = async (data: DbSemanticRequest) => {
  const response = await xiorInstanceToken.post<DbSemantic>("/DbSemantic", data);
  return response.data;
};

export const deleteSemantic = async (id: number) => {
  const response = await xiorInstanceToken.delete(`/DbSemantic/${id}`);
  return response.data;
};

export const getSemanticModel = async (dbId: number) => {
  const response = await xiorInstanceToken.get<DbSemanticModel>(`/DbSemantic/${dbId}/model`);
  return response.data;
};

export const upsertSemanticRelationship = async (data: DbSemanticRelationship) => {
  const response = await xiorInstanceToken.post<DbSemanticRelationship>("/DbSemantic/relationship", data);
  return response.data;
};

export const deleteSemanticRelationship = async (id: number) => {
  await xiorInstanceToken.delete(`/DbSemantic/relationship/${id}`);
};

export const upsertSemanticMetric = async (data: DbSemanticMetric) => {
  const response = await xiorInstanceToken.post<DbSemanticMetric>("/DbSemantic/metric", data);
  return response.data;
};

export const deleteSemanticMetric = async (id: number) => {
  await xiorInstanceToken.delete(`/DbSemantic/metric/${id}`);
};
