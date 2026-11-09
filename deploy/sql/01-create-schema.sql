-- ============================================================================
-- My Demo Corp HR PoC — SQL Server Database Schema (T-SQL)
-- Target Platform: Microsoft SQL Server
-- ============================================================================

IF DB_ID(N'HrPocDb') IS NULL
BEGIN
    CREATE DATABASE [HrPocDb];
END
GO

USE [HrPocDb];
GO

IF OBJECT_ID(N'dbo.Locations', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Locations] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Locations] PRIMARY KEY,
        [Name] NVARCHAR(300) NOT NULL,
        [StructuredName] NVARCHAR(400) NOT NULL,
        [Description] NVARCHAR(500) NULL,
        [CountryShortcut] NVARCHAR(16) NULL,
        [ZipCode] NVARCHAR(32) NULL,
        [City] NVARCHAR(150) NULL,
        [Street] NVARCHAR(250) NULL,
        [Phone] NVARCHAR(64) NULL,
        [Fax] NVARCHAR(64) NULL,
        [ManagerUsername] NVARCHAR(64) NULL,
        [AssistantManagerUsername] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Locations_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Locations_UpdatedAt] DEFAULT (SYSUTCDATETIME())
    );

    CREATE UNIQUE INDEX [IX_Locations_StructuredName] ON [dbo].[Locations]([StructuredName]);
    CREATE INDEX [IX_Locations_CountryShortcut] ON [dbo].[Locations]([CountryShortcut]);
    CREATE INDEX [IX_Locations_City] ON [dbo].[Locations]([City]);
END
GO

IF OBJECT_ID(N'dbo.Departments', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Departments] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Departments] PRIMARY KEY,
        [Name] NVARCHAR(200) NOT NULL,
        [ShortName] NVARCHAR(100) NULL,
        [StructuredName] NVARCHAR(400) NOT NULL,
        [ParentStructuredName] NVARCHAR(400) NULL,
        [ParentDepartmentId] INT NULL,
        [FunctionalShortcut] NVARCHAR(64) NULL,
        [TreeLevel] INT NOT NULL CONSTRAINT [DF_Departments_TreeLevel] DEFAULT (1),
        [ManagerUsername] NVARCHAR(64) NULL,
        [DeputyManagerUsername] NVARCHAR(64) NULL,
        [AdContainerForNewHires] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Departments_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Departments_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_Departments_Parent] FOREIGN KEY ([ParentDepartmentId]) REFERENCES [dbo].[Departments]([Id])
    );

    CREATE UNIQUE INDEX [IX_Departments_StructuredName] ON [dbo].[Departments]([StructuredName]);
    CREATE INDEX [IX_Departments_ShortName] ON [dbo].[Departments]([ShortName]);
    CREATE INDEX [IX_Departments_ParentDepartmentId] ON [dbo].[Departments]([ParentDepartmentId]);
END
GO

IF OBJECT_ID(N'dbo.CostCenters', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CostCenters] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CostCenters] PRIMARY KEY,
        [Code] NVARCHAR(64) NOT NULL,
        [StructuredName] NVARCHAR(200) NOT NULL,
        [ShortName] NVARCHAR(200) NULL,
        [ParentStructuredName] NVARCHAR(200) NULL,
        [ParentCostCenterId] INT NULL,
        [CompanyMainStructure] NVARCHAR(64) NULL,
        [CompanyCountryStructure] NVARCHAR(64) NULL,
        [TreeLevel] INT NOT NULL CONSTRAINT [DF_CostCenters_TreeLevel] DEFAULT (1),
        [ManagerUsername] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_CostCenters_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_CostCenters_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_CostCenters_Parent] FOREIGN KEY ([ParentCostCenterId]) REFERENCES [dbo].[CostCenters]([Id])
    );

    CREATE INDEX [IX_CostCenters_StructuredName] ON [dbo].[CostCenters]([StructuredName]);
    CREATE INDEX [IX_CostCenters_Code] ON [dbo].[CostCenters]([Code]);
    CREATE INDEX [IX_CostCenters_ParentCostCenterId] ON [dbo].[CostCenters]([ParentCostCenterId]);
END
GO

IF OBJECT_ID(N'dbo.Employees', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Employees] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Employees] PRIMARY KEY,
        [Username] NVARCHAR(64) NOT NULL,
        [PersonnelNumber] NVARCHAR(32) NOT NULL,
        [FirstName] NVARCHAR(100) NOT NULL,
        [LastName] NVARCHAR(100) NOT NULL,
        [Initials] NVARCHAR(16) NULL,
        [Email] NVARCHAR(200) NULL,
        [JobTitle] NVARCHAR(150) NULL,
        [EmploymentStatus] NVARCHAR(32) NOT NULL CONSTRAINT [DF_Employees_Status] DEFAULT (N'Active'),
        [HireDate] DATE NOT NULL,
        [TerminationDate] DATE NULL,
        [Phone] NVARCHAR(64) NULL,
        [Fax] NVARCHAR(64) NULL,
        [Street] NVARCHAR(250) NULL,
        [City] NVARCHAR(150) NULL,
        [ZipCode] NVARCHAR(32) NULL,
        [DepartmentId] INT NULL,
        [CostCenterId] INT NULL,
        [LocationId] INT NULL,
        [DepartmentStructuredName] NVARCHAR(400) NULL,
        [CostCenterStructuredName] NVARCHAR(200) NULL,
        [LocationStructuredName] NVARCHAR(400) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Employees_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Employees_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_Employees_Departments] FOREIGN KEY ([DepartmentId]) REFERENCES [dbo].[Departments]([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Employees_CostCenters] FOREIGN KEY ([CostCenterId]) REFERENCES [dbo].[CostCenters]([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Employees_Locations] FOREIGN KEY ([LocationId]) REFERENCES [dbo].[Locations]([Id]) ON DELETE SET NULL
    );

    CREATE UNIQUE INDEX [IX_Employees_Username] ON [dbo].[Employees]([Username]);
    CREATE UNIQUE INDEX [IX_Employees_PersonnelNumber] ON [dbo].[Employees]([PersonnelNumber]);
    CREATE INDEX [IX_Employees_LastName] ON [dbo].[Employees]([LastName]);
    CREATE INDEX [IX_Employees_EmploymentStatus] ON [dbo].[Employees]([EmploymentStatus]);
    CREATE INDEX [IX_Employees_DepartmentId] ON [dbo].[Employees]([DepartmentId]);
    CREATE INDEX [IX_Employees_CostCenterId] ON [dbo].[Employees]([CostCenterId]);
    CREATE INDEX [IX_Employees_LocationId] ON [dbo].[Employees]([LocationId]);
    CREATE INDEX [IX_Employees_UpdatedAt] ON [dbo].[Employees]([UpdatedAt]);
END
GO

