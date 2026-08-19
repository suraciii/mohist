# Keep the historical WorkflowRunProfile persistence name

The persisted `WorkflowRunProfile` name stores Run Variables even though it no
longer represents a Profile. Renaming it now would rewrite production storage
without changing behavior, so the implementation keeps the historical name
until that storage is restructured for a product reason. This name is not a
second domain meaning of Workflow Profile.
