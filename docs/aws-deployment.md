# AWS deployment

This repository is prepared for AWS deployment in `ap-southeast-1` (Singapore). The stack routes Vercel browser requests through an HTTPS CloudFront hostname to an ALB, then to separate ECS Fargate services for the .NET API and Python OCR. SQL Server is private in RDS; scan files persist on encrypted EFS. GitHub Actions uses OIDC, not long-lived AWS access keys.

No AWS resources are created by this repository change. The CloudFormation stack creates billable resources even while ECS desired count is zero: RDS, the load balancer, CloudFront, EFS mount targets, and logs. Fargate charges begin when the deployment workflow starts the services. Estimate the selected region and workload with the [AWS Pricing Calculator](https://calculator.aws/) before creating the stack.

## Important data-safety limitation

The current application has no user authentication. CORS restricts browser origins and the CloudFront-to-ALB origin header blocks requests that bypass CloudFront, but neither authenticates API callers. Anyone who knows the CloudFront API URL can call its endpoints directly, including scan-history reads, deletes, and image submissions. Do not store real or sensitive vehicle/person data on this deployment until application authentication and authorization are added.

## 1. Create and secure an AWS account

1. Create an AWS account and enable MFA for the root user. Do not use the root user for routine operations.
2. Set a billing budget and alerts before provisioning.
3. Use `ap-southeast-1` for both CloudFormation stacks and the application resources.

## 2. Create the GitHub OIDC role

1. In AWS CloudFormation, create a stack from `infra/aws/github-oidc-bootstrap.yml`.
2. Keep `StackName` as `smartparking-prod`, `RepositoryOwner` as `khoadangtran141006-svg`, `RepositoryName` as `SMART_PARKING`, and `DeploymentBranch` as `DangkhoiJR` unless those values change in GitHub.
3. If this AWS account has no GitHub OIDC provider, leave `CreateOidcProvider` set to `true`. If one already exists, set it to `false` and provide its ARN in `ExistingOidcProviderArn`.
4. After the stack completes, copy the `GitHubDeployRoleArn` output.
5. In GitHub, open **SMART_PARKING → Settings → Secrets and variables → Actions → Variables** and add `AWS_DEPLOY_ROLE_ARN` with that output. Optionally add `AWS_REGION=ap-southeast-1` and `AWS_STACK_NAME=smartparking-prod`. Do not add AWS access keys.

The trust policy only accepts GitHub Actions tokens for this repository and the `DangkhoiJR` branch.

## 3. Create the application stack

1. In CloudFormation, create a stack from `infra/aws/stack.yml` in `ap-southeast-1`.
2. Set `VercelAllowedOrigins` to the exact production Vercel origin, for example `https://your-project.vercel.app`, with no path or trailing slash. Multiple exact origins can be comma-separated. Do not use `*`.
3. Set `OriginHeaderSecret` to a fresh random 32-128 character value containing only letters, numbers, `_`, or `-`. Use a password manager; the value is a NoEcho parameter and must not be committed.
4. Keep `DesiredCount=0` for initial stack creation. Choose the RDS instance class and allocated storage only after checking the cost estimate. The template creates a private SQL Server Express instance, separate application login secret, encrypted EFS, ECR repositories, and ECS services.
5. Wait for `CREATE_COMPLETE`. CloudFront distribution creation can take longer than the other resources.

The deployment workflow uses the stack's least-privilege CloudFormation service role to update image tags and ECS services. Infrastructure changes outside those ECS updates should be made deliberately in CloudFormation, not by editing AWS resources manually.

## 4. Deploy containers and initialize SQL Server

After the stack exists and the GitHub repository variable is set, merge the deployment branch into `DangkhoiJR` to trigger the workflow, or run **Actions → Deploy SMART_PARKING to AWS** on `DangkhoiJR`.

The workflow:

1. Builds the .NET and Python images and pushes immutable run-specific tags to ECR.
2. Runs the idempotent SQL initializer inside the VPC. It applies `database/init_smartparking_sqlserver.sql` and creates the restricted `smartparking_app` login.
3. Updates the stack's image tags and starts one task for each service.
4. Waits for ECS to report both services stable.

The first deployment can take several minutes while Fargate tasks start and the OCR model loads. Check the workflow logs and the ECS/CloudWatch log groups if it fails.

## 5. Connect the Vercel frontend

1. Copy the `ApiBaseUrl` output from the application stack. It is an HTTPS CloudFront URL.
2. In the Vercel project settings, set `VITE_API_ORIGIN` to that URL for the Production environment. Add it to Preview only if preview builds should call the production API.
3. Ensure the same exact Vercel origin is included in the CloudFormation `VercelAllowedOrigins` parameter. If that parameter changes, update the stack so ECS receives the new CORS allowlist.
4. Redeploy the Vercel frontend so Vite embeds the new `VITE_API_ORIGIN` value.
5. The GitHub workflow sets `DesiredCount=1` through CloudFormation after database initialization, so the stack continues to track the running services.

Without `VITE_API_ORIGIN`, local development continues using the Vite proxy paths.

## 6. Cost and cleanup

RDS SQL Server, ALB, CloudFront, EFS storage/mount targets, Fargate tasks, data transfer, backups, ECR images, and CloudWatch logs can all incur charges. The template retains EFS data and snapshots the RDS instance on stack deletion; remove those retained resources deliberately after backing up anything needed. Do not delete the stack as a substitute for first checking the retained file system and snapshot.
